using System.IO;
using System.Management;
using LibreHardwareMonitor.Hardware.Storage;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Info;

public static class StorageInfo
{
    public sealed class Attr
    {
        public byte Id;
        public string Name = "";
        public int Current, Worst, Threshold;
        public ulong Raw;
        public bool Nvme;
    }

    public sealed class Part
    {
        public string Letter = "";
        public string Label = "";
        public string FileSystem = "";
        public long Size, Free;
    }

    public sealed class Disk
    {
        public int Number = -1;
        public string Model = "", Vendor = "", Serial = "", Firmware = "", Kind = "", Bus = "", Interface = "";
        public ulong Size;
        public Health Status;
        public int? Life, Temp, TempWarn, TempCrit;
        public ulong? HostWritesGB, HostReadsGB;
        public ulong PowerOnHours, PowerOnCount;
        public bool Trim;
        public bool SmartAvailable;
        public List<Attr> Attributes = new();
        public List<Part> Partitions = new();
        public string? PnpId;
    }

    public static async Task<List<Disk>> ReadAsync()
    {
        var hub = SensorHub.Instance;
        var disks = new List<Disk>();
        try
        {
            disks = await hub.InvokeAsync(pc =>
            {
                var list = new List<Disk>();
                if (pc == null) return list;
                foreach (var h in pc.Hardware.OfType<StorageDevice>())
                {
                    try
                    {
                        var s = h.Storage;
                        var d = new Disk
                        {
                            Number = s.DriveNumber,
                            Model = s.Model?.Trim() ?? h.Name,
                            Vendor = s.Vendor?.Trim() ?? "",
                            Serial = s.SerialNumber?.Trim() ?? "",
                            Firmware = (s.FirmwareRev ?? s.Firmware ?? "").Trim(),
                            Size = s.TotalSize,
                            Kind = s.IsNVMe ? "NVMe SSD" : s.IsSSD ? "SATA SSD" : "HD",
                            Bus = s.BusType.ToString().Replace("BusType", ""),
                            Trim = s.IsTrimSupported,
                        };
                        if (!s.IsNVMe && !s.IsSSD && s.BusType == DiskInfoToolkit.Enums.Interop.StorageBusType.BusTypeUsb) d.Kind = Loc.T("Disco USB", "USB drive");
                        if (!s.IsNVMe)
                            d.Interface = s.ATAInfo?.TransferMode switch
                            {
                                DiskInfoToolkit.Enums.Interop.TransferMode.TransferModeSata600 => "SATA III (6 Gb/s)",
                                DiskInfoToolkit.Enums.Interop.TransferMode.TransferModeSata300 => "SATA II (3 Gb/s)",
                                DiskInfoToolkit.Enums.Interop.TransferMode.TransferModeSata150 => "SATA I (1,5 Gb/s)",
                                _ => d.Bus,
                            };
                        var sm = s.Smart;
                        if (sm != null)
                        {
                            d.SmartAvailable = sm.SmartAttributes is { Count: > 0 };
                            d.Status = sm.DiskStatus switch
                            {
                                DiskInfoToolkit.Enums.Interop.DiskStatus.Good => Health.Good,
                                DiskInfoToolkit.Enums.Interop.DiskStatus.Caution => Health.Warning,
                                DiskInfoToolkit.Enums.Interop.DiskStatus.Bad => Health.Critical,
                                _ => Health.Unknown,
                            };
                            d.Life = sm.Life is sbyte l && l >= 0 && l <= 100 ? l : null;
                            d.Temp = sm.Temperature is int t && t > 0 && t < 120 ? t : null;
                            d.TempWarn = sm.TemperatureWarning is int tw && tw > 0 ? tw : null;
                            d.TempCrit = sm.TemperatureCritical is int tc && tc > 0 ? tc : null;
                            d.HostWritesGB = sm.HostWrites is ulong hw && hw > 0 ? hw : null;
                            d.HostReadsGB = sm.HostReads is ulong hr && hr > 0 ? hr : null;
                            d.PowerOnCount = sm.PowerOnCount;
                            d.PowerOnHours = sm.MeasuredPowerOnHours > 0 ? sm.MeasuredPowerOnHours : sm.DetectedPowerOnHours;
                            foreach (var a in sm.SmartAttributes ?? new())
                            {
                                var st = a.Attribute;
                                d.Attributes.Add(new Attr
                                {
                                    Id = st.ID,
                                    Name = a.Info?.Name ?? "",
                                    Current = st.CurrentValue,
                                    Worst = st.WorstValue,
                                    Threshold = st.Threshold,
                                    Raw = st.RawValueULong,
                                    Nvme = s.IsNVMe,
                                });
                            }
                        }
                        foreach (var p in s.Partitions ?? new())
                        {
                            if (p.DriveLetter is char c)
                                d.Partitions.Add(new Part { Letter = c + ":", Size = p.PartitionLength, Free = (long)(p.AvailableFreeSpace ?? 0) });
                        }
                        list.Add(d);
                    }
                    catch (Exception ex) { Paths.LogError("StorageInfo.device", ex); }
                }
                return list;
            });
        }
        catch (Exception ex) { Paths.LogError("StorageInfo.lhm", ex); }

        // Sem administrador o SMART não é acessível; completamos com o que o Windows informa.
        try { await Task.Run(() => FillFromWmi(disks)); } catch (Exception ex) { Paths.LogError("StorageInfo.wmi", ex); }
        return disks.OrderBy(d => d.Number).ToList();
    }

    static void FillFromWmi(List<Disk> disks)
    {
        var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size, SerialNumber, FirmwareVersion FROM MSFT_PhysicalDisk")))
        {
            foreach (ManagementObject o in s.Get())
            {
                if (!int.TryParse(o["DeviceId"]?.ToString(), out int num)) continue;
                var d = disks.FirstOrDefault(x => x.Number == num);
                if (d == null)
                {
                    d = new Disk { Number = num };
                    disks.Add(d);
                }
                if (d.Model == "") d.Model = (o["FriendlyName"] as string ?? "").Trim();
                if (d.Serial == "") d.Serial = (o["SerialNumber"] as string ?? "").Trim();
                if (d.Firmware == "") d.Firmware = (o["FirmwareVersion"] as string ?? "").Trim();
                if (d.Size == 0) d.Size = Convert.ToUInt64(o["Size"] ?? 0UL);
                int bus = Convert.ToInt32(o["BusType"] ?? 0);
                int media = Convert.ToInt32(o["MediaType"] ?? 0);
                if (d.Bus == "") d.Bus = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", 13 => "MMC", _ => "" };
                if (d.Kind == "") d.Kind = bus == 17 ? "NVMe SSD" : bus == 7 ? Loc.T("Disco USB", "USB drive") : media == 4 ? "SATA SSD" : media == 3 ? "HD" : "";
                if (d.Status == Health.Unknown)
                    d.Status = Convert.ToInt32(o["HealthStatus"] ?? 5) switch { 0 => Health.Good, 1 => Health.Warning, 2 => Health.Critical, _ => Health.Unknown };
            }
        }

        if (disks.Any(d => d.Partitions.Count == 0))
        {
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter, Size FROM MSFT_Partition"));
            foreach (ManagementObject o in s.Get())
            {
                int num = Convert.ToInt32(o["DiskNumber"] ?? -1);
                char letter = Convert.ToChar(o["DriveLetter"] ?? '\0');
                var d = disks.FirstOrDefault(x => x.Number == num);
                if (d == null || letter == '\0' || d.Partitions.Any(p => p.Letter[0] == letter)) continue;
                d.Partitions.Add(new Part { Letter = letter + ":", Size = Convert.ToInt64(o["Size"] ?? 0L) });
            }
        }

        foreach (var d in disks)
        {
            foreach (var p in d.Partitions)
            {
                try
                {
                    var di = new DriveInfo(p.Letter);
                    if (!di.IsReady) continue;
                    p.Label = di.VolumeLabel;
                    p.FileSystem = di.DriveFormat;
                    p.Size = di.TotalSize;
                    p.Free = di.AvailableFreeSpace;
                }
                catch { }
            }
        }

        using (var s = new ManagementObjectSearcher("SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
        {
            foreach (ManagementObject o in s.Get())
            {
                int idx = Convert.ToInt32(o["Index"] ?? -1);
                var d = disks.FirstOrDefault(x => x.Number == idx);
                if (d == null) continue;
                d.PnpId = o["PNPDeviceID"] as string;
                if (d.Kind.StartsWith("NVMe") && d.PnpId != null && Pcie.ReadAncestor(d.PnpId) is Pcie.Link l)
                    d.Interface = GpuInfo.PcieText(l).Replace("PCIe", "PCIe").Trim() + " — NVMe";
            }
        }
        foreach (var d in disks.Where(d => d.Interface == "")) d.Interface = d.Bus;
    }

    // ------------------------------------------------------------- SMART explicado

    record AttrText(string Pt, string En, string TipPt, string TipEn, bool Critical = false);

    static readonly Dictionary<byte, AttrText> Ata = new()
    {
        [0x01] = new("Taxa de erros de leitura", "Read error rate", "Erros ao ler dados da superfície. Em HDs Seagate o valor bruto é naturalmente alto; observe o valor normalizado.", "Errors while reading the surface. On Seagate drives the raw value is naturally high; check the normalized value."),
        [0x02] = new("Desempenho de throughput", "Throughput performance", "Desempenho geral do disco.", "Overall drive performance."),
        [0x03] = new("Tempo para girar", "Spin-up time", "Tempo para o disco atingir a rotação de trabalho.", "Time to reach operating speed."),
        [0x04] = new("Partidas/paradas", "Start/stop count", "Quantas vezes o motor ligou e desligou.", "Number of spindle start/stop cycles."),
        [0x05] = new("Setores realocados", "Reallocated sectors", "Setores defeituosos substituídos por reservas. Qualquer valor acima de zero indica desgaste; se crescer, faça backup.", "Bad sectors replaced by spares. Anything above zero indicates wear; if it grows, back up your data.", true),
        [0x07] = new("Taxa de erros de busca", "Seek error rate", "Erros ao posicionar a cabeça de leitura.", "Errors positioning the read head."),
        [0x09] = new("Horas ligado", "Power-on hours", "Tempo total que o disco ficou ligado.", "Total time the drive has been powered on."),
        [0x0A] = new("Tentativas de rotação", "Spin retry count", "Tentativas extras para o disco girar. Valores acima de zero indicam problema mecânico.", "Extra attempts to spin up. Non-zero means a mechanical problem.", true),
        [0x0B] = new("Tentativas de calibração", "Calibration retry count", "Tentativas de recalibração da cabeça.", "Head recalibration retries."),
        [0x0C] = new("Vezes ligado", "Power cycle count", "Quantas vezes o disco foi ligado.", "How many times the drive was powered on."),
        [0x0D] = new("Erros de leitura suaves", "Soft read error rate", "Erros de leitura corrigidos.", "Corrected read errors."),
        [0xA0] = new("Setores incorrigíveis (SSD)", "Uncorrectable sectors (SSD)", "Setores que não puderam ser corrigidos.", "Sectors that could not be corrected.", true),
        [0xA5] = new("Total de blocos apagados", "Total erase count", "Desgaste acumulado da memória flash.", "Accumulated flash wear."),
        [0xA9] = new("Vida restante", "Remaining life", "Estimativa de vida útil restante do SSD.", "Estimated remaining SSD life."),
        [0xAA] = new("Blocos reserva disponíveis", "Available reserved space", "Blocos reserva para substituir falhas.", "Spare blocks available to replace failures."),
        [0xAB] = new("Falhas de programação", "Program fail count", "Falhas ao gravar na memória flash.", "Failures writing to flash.", true),
        [0xAC] = new("Falhas de apagamento", "Erase fail count", "Falhas ao apagar blocos da memória flash.", "Failures erasing flash blocks.", true),
        [0xAD] = new("Contagem de desgaste", "Wear leveling count", "Média de ciclos de apagamento por bloco. Indica o desgaste do SSD.", "Average erase cycles per block. Indicates SSD wear."),
        [0xAE] = new("Desligamentos inesperados", "Unexpected power loss", "Vezes que a energia caiu sem desligamento correto.", "Times power was lost without a proper shutdown."),
        [0xAF] = new("Falhas de programação (dado)", "Program fail count (chip)", "Falhas de gravação no chip.", "Chip write failures.", true),
        [0xB1] = new("Contagem de desgaste", "Wear range delta", "Diferença de desgaste entre os blocos.", "Wear difference between blocks."),
        [0xB3] = new("Blocos reserva usados", "Used reserved blocks", "Blocos reserva já consumidos.", "Spare blocks already consumed.", true),
        [0xB4] = new("Blocos reserva livres", "Unused reserved blocks", "Blocos reserva ainda disponíveis.", "Spare blocks still available."),
        [0xB5] = new("Falhas de programação", "Program fail count", "Falhas de gravação.", "Write failures.", true),
        [0xB6] = new("Falhas de apagamento", "Erase fail count", "Falhas de apagamento.", "Erase failures.", true),
        [0xB7] = new("Redução de velocidade SATA", "SATA downshift errors", "Vezes que o link SATA caiu para uma velocidade menor. Pode indicar cabo ruim.", "Times the SATA link downshifted. May indicate a bad cable."),
        [0xB8] = new("Erros de ponta a ponta", "End-to-end errors", "Erros de integridade entre cache e mídia.", "Data integrity errors between cache and media.", true),
        [0xBB] = new("Erros incorrigíveis reportados", "Reported uncorrectable errors", "Erros que o ECC não conseguiu corrigir.", "Errors ECC could not correct.", true),
        [0xBC] = new("Tempo limite de comandos", "Command timeout", "Comandos que demoraram demais. Pode indicar cabo/fonte com problema.", "Commands that timed out. May indicate cable/PSU issues."),
        [0xBD] = new("Gravações em altura elevada", "High fly writes", "Cabeça gravando fora da altura ideal.", "Head writing outside normal fly height."),
        [0xBE] = new("Temperatura do fluxo de ar", "Airflow temperature", "Temperatura do ar dentro do disco.", "Air temperature inside the drive."),
        [0xBF] = new("Erros por impacto", "G-sense error rate", "Erros causados por choques ou vibração.", "Errors caused by shock or vibration."),
        [0xC0] = new("Recolhimentos por desligamento", "Power-off retract count", "Vezes que a cabeça foi recolhida por falta de energia.", "Emergency head retracts."),
        [0xC1] = new("Ciclos de carga/descarga", "Load/unload cycle count", "Vezes que a cabeça estacionou.", "Head park cycles."),
        [0xC2] = new("Temperatura", "Temperature", "Temperatura atual do disco.", "Current drive temperature."),
        [0xC3] = new("ECC recuperado por hardware", "Hardware ECC recovered", "Erros corrigidos pelo ECC.", "Errors corrected by ECC."),
        [0xC4] = new("Eventos de realocação", "Reallocation event count", "Quantas operações de realocação ocorreram.", "Number of remap operations.", true),
        [0xC5] = new("Setores pendentes", "Current pending sectors", "Setores instáveis aguardando realocação. Acima de zero é sinal de alerta.", "Unstable sectors waiting to be remapped. Above zero is a warning sign.", true),
        [0xC6] = new("Setores incorrigíveis", "Offline uncorrectable", "Setores que não puderam ser lidos nem corrigidos.", "Sectors that could not be read or corrected.", true),
        [0xC7] = new("Erros de CRC (UltraDMA)", "UltraDMA CRC errors", "Erros na transmissão pelo cabo. Geralmente cabo SATA ruim ou mal encaixado.", "Transmission errors over the cable. Usually a bad or loose SATA cable."),
        [0xC8] = new("Taxa de erros de escrita", "Write error rate", "Erros ao gravar.", "Errors while writing."),
        [0xCA] = new("Vida útil consumida", "Percent lifetime used", "Percentual de vida útil já consumido (SSD).", "Percentage of lifetime used (SSD)."),
        [0xD2] = new("RAIN bem-sucedido", "Successful RAIN recovery", "Recuperações internas bem-sucedidas.", "Successful internal recoveries."),
        [0xE6] = new("Vida da mídia", "Media wearout", "Desgaste da memória flash.", "Flash wear."),
        [0xE7] = new("Vida restante do SSD", "SSD life left", "Vida útil restante estimada.", "Estimated remaining life."),
        [0xE8] = new("Espaço reserva disponível", "Available reserved space", "Reserva ainda disponível.", "Remaining spare area."),
        [0xE9] = new("Indicador de desgaste", "Media wearout indicator", "Desgaste da memória flash (100 = novo).", "Flash wear (100 = new)."),
        [0xEA] = new("Média de apagamentos", "Average erase count", "Média de ciclos de apagamento.", "Average erase cycles."),
        [0xF1] = new("Total gravado", "Total LBAs written", "Quantidade total de dados gravados.", "Total data written."),
        [0xF2] = new("Total lido", "Total LBAs read", "Quantidade total de dados lidos.", "Total data read."),
        [0xF9] = new("Gravações na NAND", "NAND writes", "Dados efetivamente gravados nos chips de memória.", "Data actually written to the NAND chips."),
        [0xFE] = new("Proteção contra queda", "Free fall protection", "Vezes que a proteção contra queda foi acionada.", "Free fall protection events."),
    };

    static readonly Dictionary<byte, AttrText> NvmeAttr = new()
    {
        [0x01] = new("Aviso crítico", "Critical warning", "Diferente de zero indica problema grave (reserva baixa, temperatura, somente leitura…).", "Non-zero means a serious problem (low spare, temperature, read-only...).", true),
        [0x02] = new("Temperatura", "Composite temperature", "Temperatura do SSD.", "SSD temperature."),
        [0x03] = new("Reserva disponível", "Available spare", "Percentual de blocos reserva restantes.", "Percentage of spare blocks left."),
        [0x04] = new("Limite da reserva", "Available spare threshold", "Abaixo deste valor o SSD emite alerta.", "Below this value the SSD raises a warning."),
        [0x05] = new("Vida útil usada", "Percentage used", "Percentual estimado da vida útil já consumida (100% = fim da garantia de escrita).", "Estimated percentage of endurance used (100% = rated endurance reached)."),
        [0x06] = new("Dados lidos", "Data units read", "Unidades de 512.000 bytes lidas.", "Units of 512,000 bytes read."),
        [0x07] = new("Dados gravados", "Data units written", "Unidades de 512.000 bytes gravadas (TBW).", "Units of 512,000 bytes written (TBW)."),
        [0x08] = new("Comandos de leitura", "Host read commands", "Quantidade de comandos de leitura.", "Number of read commands."),
        [0x09] = new("Comandos de escrita", "Host write commands", "Quantidade de comandos de escrita.", "Number of write commands."),
        [0x0A] = new("Tempo ocupado do controlador", "Controller busy time", "Minutos com o controlador processando comandos.", "Minutes the controller spent busy."),
        [0x0B] = new("Vezes ligado", "Power cycles", "Quantas vezes o SSD foi ligado.", "Number of power cycles."),
        [0x0C] = new("Horas ligado", "Power-on hours", "Tempo total ligado.", "Total powered-on time."),
        [0x0D] = new("Desligamentos inseguros", "Unsafe shutdowns", "Vezes que a energia caiu sem desligamento correto.", "Power losses without a proper shutdown."),
        [0x0E] = new("Erros de integridade", "Media and data integrity errors", "Erros de dados não recuperados. Acima de zero é preocupante.", "Unrecovered data errors. Above zero is concerning.", true),
        [0x0F] = new("Registros de erro", "Error log entries", "Entradas no log de erros do controlador (normalmente inofensivo).", "Controller error log entries (usually harmless)."),
    };

    static (string name, string? tip, bool critical) Describe(Attr a)
    {
        var dict = a.Nvme ? NvmeAttr : Ata;
        if (dict.TryGetValue(a.Id, out var t))
            return (Loc.T(t.Pt, t.En), Loc.T(t.TipPt, t.TipEn), t.Critical);
        return (string.IsNullOrEmpty(a.Name) ? Loc.T("Específico do fabricante", "Vendor specific") : a.Name, Loc.T("Atributo específico do fabricante.", "Vendor-specific attribute."), false);
    }

    public static async Task<List<InfoPage>> BuildPagesAsync()
    {
        var L = Loc.T;
        var disks = await ReadAsync();
        var snap = SensorHub.Instance.Last;
        var pages = new List<InfoPage>();
        foreach (var d in disks)
        {
            var p = new InfoPage { Title = d.Model == "" ? L($"Disco {d.Number}", $"Disk {d.Number}") : d.Model };

            var health = p.Group(L("Saúde", "Health"));
            health.Status = d.Status;
            health.Add(L("Status", "Status"), HealthText.Of(d.Status) + (d.Life is int life ? $" ({life}%)" : ""),
                L("Avaliação baseada nos dados SMART: Bom, Atenção (sinais de desgaste ou setores com problema) ou Crítico (faça backup imediatamente).", "Based on SMART data: Good, Warning (wear or problematic sectors) or Critical (back up immediately)."), d.Status);
            health.Add(L("Saúde", "Health"), d.Life is int lf ? $"{lf}%" : null, L("Vida útil restante estimada pelo próprio disco.", "Remaining life estimated by the drive itself."));
            health.Add(L("Temperatura", "Temperature"), d.Temp is int t ? Fmt.Value(t, Unit.Temp) : null,
                L("SSDs ficam bem até ~70 °C; HDs preferem abaixo de 50 °C.", "SSDs are fine up to ~70 °C; HDDs prefer below 50 °C."),
                d.Temp is int tt ? (tt >= (d.TempCrit ?? (d.Kind.Contains("SSD") ? 80 : 60)) ? Health.Critical : tt >= (d.TempWarn ?? (d.Kind.Contains("SSD") ? 70 : 50)) ? Health.Warning : Health.Good) : Health.Unknown);
            health.Add(L("Horas ligado", "Power-on hours"), d.PowerOnHours > 0 ? $"{Fmt.N(d.PowerOnHours)} h (~{Fmt.N(d.PowerOnHours / 24.0 / 365, 1)} {L("anos", "years")})" : null);
            health.Add(L("Vezes ligado", "Power-on count"), d.PowerOnCount > 0 ? Fmt.N(d.PowerOnCount) : null);
            health.Add(L("Total gravado (TBW)", "Total written (TBW)"), d.HostWritesGB is ulong w ? Fmt.Capacity(w * 1e9) : null,
                L("Quantidade de dados já gravados. SSDs têm um limite de gravação (TBW) informado pelo fabricante.", "Data written so far. SSDs have an endurance rating (TBW) set by the manufacturer."));
            health.Add(L("Total lido", "Total read"), d.HostReadsGB is ulong r ? Fmt.Capacity(r * 1e9) : null);
            if (d.Life is int lf2 && lf2 < 100 && d.PowerOnHours > 500)
            {
                double hoursPerPct = d.PowerOnHours / (double)(100 - lf2);
                double years = hoursPerPct * lf2 / 24 / 365;
                health.Add(L("Vida útil restante estimada", "Estimated remaining life"), years > 50 ? L("mais de 50 anos no ritmo atual", "over 50 years at current pace") : L($"~{Fmt.N(years, 1)} anos no ritmo de uso atual", $"~{Fmt.N(years, 1)} years at current usage"),
                    L("Projeção simples baseada no desgaste por hora até agora. Não é garantia.", "Simple projection from wear per hour so far. Not a guarantee."));
            }
            int realloc = (int)(d.Attributes.FirstOrDefault(a => !a.Nvme && a.Id == 0x05)?.Raw ?? 0);
            int pending = (int)(d.Attributes.FirstOrDefault(a => !a.Nvme && a.Id == 0xC5)?.Raw ?? 0);
            int uncorr = (int)(d.Attributes.FirstOrDefault(a => !a.Nvme && a.Id == 0xC6)?.Raw ?? 0);
            ulong nvErr = d.Attributes.FirstOrDefault(a => a.Nvme && a.Id == 0x0E)?.Raw ?? 0;
            if (d.SmartAvailable)
            {
                if (d.Attributes.Any(a => a.Nvme))
                    health.Add(L("Erros de integridade de mídia", "Media integrity errors"), Fmt.N(nvErr), null, nvErr > 0 ? Health.Warning : Health.Good);
                else
                {
                    health.Add(L("Setores realocados", "Reallocated sectors"), Fmt.N(realloc), L("Setores defeituosos já substituídos.", "Bad sectors already replaced."), realloc > 0 ? Health.Warning : Health.Good);
                    health.Add(L("Setores pendentes / incorrigíveis", "Pending / uncorrectable sectors"), $"{pending} / {uncorr}", null, pending + uncorr > 0 ? Health.Warning : Health.Good);
                }
            }
            if (!d.SmartAvailable)
                health.Notes.Add((Health.Unknown, Native.IsAdmin
                    ? L("Este disco não forneceu dados SMART (comum em pendrives, cartões e alguns adaptadores USB).", "This drive did not provide SMART data (common on flash drives, cards and some USB adapters).")
                    : L("Abra o Pulso como administrador para ler os dados SMART completos.", "Run Pulso as administrator to read full SMART data.")));

            var id = p.Group(L("Identificação", "Identification"));
            id.Add(L("Modelo", "Model"), d.Model);
            id.Add(L("Fabricante", "Manufacturer"), d.Vendor != "" ? d.Vendor : GuessVendor(d.Model));
            id.Add(L("Número de série", "Serial number"), d.Serial);
            id.Add("Firmware", d.Firmware, L("Software interno do disco. Atualizações do fabricante podem corrigir bugs.", "The drive's internal software. Vendor updates can fix bugs."));
            id.Add(L("Tipo", "Type"), d.Kind, L("NVMe: SSD rápido conectado via PCIe. SATA SSD: SSD no padrão antigo, até ~550 MB/s. HD: disco mecânico, mais lento.", "NVMe: fast PCIe SSD. SATA SSD: older standard, up to ~550 MB/s. HDD: mechanical, slower."));
            id.Add(L("Interface", "Interface"), d.Interface);
            id.Add(L("Capacidade", "Capacity"), d.Size > 0 ? Fmt.Capacity(d.Size) : null);
            if (d.Kind.Contains("SSD")) id.Add("TRIM", Fmt.YesNo(d.Trim), L("Recurso que mantém o SSD rápido ao longo do tempo.", "Keeps the SSD fast over time."));
            var live = snap?.Disks.FirstOrDefault(x => x.Name == d.Model);
            if (live?.Activity != null) id.Add(L("Atividade agora", "Activity now"), Fmt.Value(live.Activity, Unit.Percent));

            if (d.Partitions.Count > 0)
            {
                var pg = p.Group(L("Partições", "Partitions"));
                var tb = new InfoTable { Columns = new[] { L("Unidade", "Drive"), L("Nome", "Label"), L("Sistema", "File system"), L("Total", "Total"), L("Usado", "Used"), L("Livre", "Free"), "%" } };
                foreach (var pt in d.Partitions.OrderBy(x => x.Letter))
                {
                    double pct = pt.Size > 0 ? (pt.Size - pt.Free) * 100.0 / pt.Size : 0;
                    tb.Add(null, pct > 95 ? Health.Critical : pct > 85 ? Health.Warning : Health.Good,
                        pt.Letter, pt.Label, pt.FileSystem, Fmt.Bytes(pt.Size), Fmt.Bytes(pt.Size - pt.Free), Fmt.Bytes(pt.Free), $"{Fmt.N(pct)}%");
                }
                pg.Tables.Add(tb);
            }

            if (d.Attributes.Count > 0)
            {
                var sg = p.Group(L("Dados SMART", "SMART data"));
                bool nvme = d.Attributes[0].Nvme;
                var tb = nvme
                    ? new InfoTable { Columns = new[] { "ID", L("Atributo", "Attribute"), L("Valor bruto", "Raw value") } }
                    : new InfoTable { Columns = new[] { "ID", L("Atributo", "Attribute"), L("Atual", "Current"), L("Pior", "Worst"), L("Limite", "Threshold"), L("Valor bruto", "Raw value") } };
                foreach (var a in d.Attributes)
                {
                    var (name, tip, critical) = Describe(a);
                    var st = Health.Good;
                    if (!nvme && a.Threshold > 0 && a.Current <= a.Threshold) st = Health.Critical;
                    else if (critical && a.Raw > 0 && !(nvme && a.Id == 0x0F)) st = Health.Warning;
                    if (nvme)
                        tb.Add(tip, st, a.Id.ToString("X2"), name, Fmt.N(a.Raw));
                    else
                        tb.Add(tip, st, a.Id.ToString("X2"), name, a.Current.ToString(), a.Worst.ToString(), a.Threshold.ToString(), Fmt.N(a.Raw & 0xFFFFFFFFFFFF));
                }
                sg.Tables.Add(tb);
            }
            pages.Add(p);
        }
        if (pages.Count == 0)
        {
            var p = new InfoPage { Title = L("Armazenamento", "Storage") };
            p.Group(L("Discos", "Drives")).Add(L("Status", "Status"), L("Nenhum disco encontrado", "No drives found"));
            pages.Add(p);
        }
        return pages;
    }

    static string GuessVendor(string model)
    {
        string m = model.ToUpperInvariant();
        (string k, string v)[] map =
        {
            ("SAMSUNG", "Samsung"), ("WDC", "Western Digital"), ("WD ", "Western Digital"), ("WESTERN", "Western Digital"), ("ST", "Seagate"), ("KINGSTON", "Kingston"),
            ("CRUCIAL", "Crucial"), ("CT", "Crucial"), ("KIOXIA", "Kioxia"), ("TOSHIBA", "Toshiba"), ("INTEL", "Intel"), ("SANDISK", "SanDisk"), ("ADATA", "ADATA"),
            ("XPG", "ADATA"), ("HYNIX", "SK hynix"), ("SK HYNIX", "SK hynix"), ("MICRON", "Micron"), ("HITACHI", "Hitachi"), ("HGST", "HGST"), ("NETAC", "Netac"),
            ("LEXAR", "Lexar"), ("PNY", "PNY"), ("CORSAIR", "Corsair"), ("TEAM", "Team Group"), ("PATRIOT", "Patriot"), ("SABRENT", "Sabrent"), ("HP", "HP"), ("KBG", "Kioxia"),
        };
        foreach (var (k, v) in map) if (m.StartsWith(k) || m.Contains(" " + k)) return v;
        return "";
    }
}

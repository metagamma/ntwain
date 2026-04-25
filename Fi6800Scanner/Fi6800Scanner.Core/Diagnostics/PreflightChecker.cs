using System;
using System.IO;
using Microsoft.Win32;

namespace Fi6800Scanner.Core.Diagnostics
{
    public static class PreflightChecker
    {
        public static PreflightResult Run()
        {
            var result = new PreflightResult
            {
                OsVersion = Environment.OSVersion.VersionString,
                IsApp64Bit = Environment.Is64BitProcess
            };

            CheckPaperStream(result);
            CheckTwainDsm(result);
            CheckWindows11Update(result);

            result.ArchitectureMatch =
                (result.IsApp64Bit && result.PaperStreamX64Installed) ||
                (!result.IsApp64Bit && result.PaperStreamX86Installed);

            if (!result.ArchitectureMatch)
            {
                result.Errors.Add(
                    $"Mismatch de arquitectura: app es {(result.IsApp64Bit ? "x64" : "x86")} " +
                    $"pero PaperStream IP {(result.IsApp64Bit ? "x64" : "x86")} no está instalado. " +
                    $"Instala el driver correspondiente.");
            }

            if (!result.TwainDsmAvailable)
                result.Warnings.Add("TWAINDSM.dll no encontrada. NTwain v3 prefiere DSM2; verificar instalación de Windows.");

            result.Success = result.Errors.Count == 0;
            return result;
        }

        private static void CheckPaperStream(PreflightResult result)
        {
            // PaperStream IP registra entradas en HKLM\SOFTWARE\PFU\* y WOW6432Node
            // Buscamos en ambas vistas del registro
            try
            {
                using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var pfu32 = hklm32.OpenSubKey(@"SOFTWARE\PFU"))
                {
                    if (pfu32 != null) result.PaperStreamX86Installed = ContainsPaperStream(pfu32);
                }

                using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var pfu64 = hklm64.OpenSubKey(@"SOFTWARE\PFU"))
                {
                    if (pfu64 != null) result.PaperStreamX64Installed = ContainsPaperStream(pfu64);
                }

                // Versión: heurística — buscar primer subkey con "Version"
                if (result.PaperStreamX86Installed || result.PaperStreamX64Installed)
                    result.PaperStreamVersion = TryReadPaperStreamVersion();
            }
            catch (Exception ex)
            {
                result.Warnings.Add("No se pudo leer registro de PaperStream: " + ex.Message);
            }
        }

        private static bool ContainsPaperStream(RegistryKey pfuKey)
        {
            foreach (var sub in pfuKey.GetSubKeyNames())
            {
                if (sub.IndexOf("PaperStream", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (sub.IndexOf("PSIP", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string TryReadPaperStreamVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) return null;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using (var sub = key.OpenSubKey(subName))
                        {
                            if (sub == null) continue;
                            var name = sub.GetValue("DisplayName") as string;
                            if (name != null && name.IndexOf("PaperStream IP", StringComparison.OrdinalIgnoreCase) >= 0)
                                return sub.GetValue("DisplayVersion") as string;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static void CheckTwainDsm(PreflightResult result)
        {
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            // En procesos x86 sobre Windows x64, System32 alias a SysWOW64 vía redirector
            string dsmName = "TWAINDSM.dll";
            result.TwainDsmAvailable =
                File.Exists(Path.Combine(sys32, dsmName)) ||
                File.Exists(Path.Combine(sysWow, dsmName));
        }

        private static void CheckWindows11Update(PreflightResult result)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return;
                    var build = key.GetValue("CurrentBuildNumber") as string;
                    var ubr = key.GetValue("UBR");
                    var displayVersion = key.GetValue("DisplayVersion") as string;

                    if (int.TryParse(build, out int b) && b >= 26100)
                    {
                        // Windows 11 24H2+: el bug del TWAIN-bridge requiere KB5055523 o posterior
                        result.Warnings.Add(
                            $"Detectado Windows 11 24H2+ (build {build}, UBR {ubr}, version {displayVersion}). " +
                            "Asegúrate de tener Windows actualizado a abril 2025 o posterior — la actualización KB5055523 " +
                            "corrige un bug en TWAIN-bridge que afectaba a escáneres Fujitsu.");
                    }
                }
            }
            catch { }
        }
    }
}

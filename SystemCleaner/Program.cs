using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SystemCleaner
{
    internal static class Program
    {
        private static int _totalFilesDeleted;
        private static int _totalDirsDeleted;
        private static int _totalFilesScheduledForDelete;
        private static int _totalDirsScheduledForDelete;
        private static readonly List<string> _errors = new();

        // ================== P/INVOKE PARA FORCE (DELETE NO REBOOT) ==================
        [Flags]
        private enum MoveFileExFlags : uint
        {
            MOVEFILE_REPLACE_EXISTING = 0x00000001,
            MOVEFILE_COPY_ALLOWED = 0x00000002,
            MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004,
            MOVEFILE_WRITE_THROUGH = 0x00000008,
            MOVEFILE_CREATE_HARDLINK = 0x00000010,
            MOVEFILE_FAIL_IF_NOT_TRACKABLE = 0x00000020
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, MoveFileExFlags dwFlags);
        // ============================================================================

        // ================== P/INVOKE PARA LIXEIRA (SHEmptyRecycleBin) ===============
        [Flags]
        private enum RecycleFlags : uint
        {
            SHERB_NOCONFIRMATION = 0x00000001,
            SHERB_NOPROGRESSUI = 0x00000002,
            SHERB_NOSOUND = 0x00000004
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(
            IntPtr hwnd,
            string? pszRootPath,
            RecycleFlags dwFlags);
        // ============================================================================

        private static void Main()
        {
            Console.Title = "System Cleaner - C# (FORCE)";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("===============================================");
            Console.WriteLine("     LIMPA TEMPORÁRIOS E CACHES DO SISTEMA    ");
            Console.WriteLine("         (com FORCE: delete no reboot)        ");
            Console.WriteLine("===============================================\n");
            Console.ResetColor();

            Console.WriteLine("ATENÇÃO:");
            Console.WriteLine("- Execute com navegadores FECHADOS.");
            Console.WriteLine("- Ideal rodar como ADMINISTRADOR.");
            Console.WriteLine("- Arquivos em uso serão marcados para exclusão no PRÓXIMO REBOOT.");
            Console.WriteLine("- Use por sua conta e risco.\n");

            Console.Write("Deseja continuar? (S/N): ");
            var key = Console.ReadKey();
            Console.WriteLine();
            if (char.ToUpperInvariant(key.KeyChar) != 'S')
            {
                Console.WriteLine("Operação cancelada pelo usuário.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                CleanTempFolders();          // Temp do usuário + Windows\Temp
                CleanBrowserCaches();        // Chrome/Edge/Brave/Opera/Firefox
                CleanWindowsLogsBasic();     // SoftwareDistribution\Download, Logs, Prefetch
                CleanWindowsCacheFolders();  // DeliveryOptimization, WER, Diagnosis, Explorer, PRINTERS, Minidump
                EmptyRecycleBin();           // Lixeira do Windows
            }
            catch (Exception ex)
            {
                LogError("Erro inesperado durante a limpeza geral", ex);
            }

            stopwatch.Stop();

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("===============================================");
            Console.WriteLine("                LIMPEZA CONCLUÍDA              ");
            Console.WriteLine("===============================================");
            Console.ResetColor();

            Console.WriteLine($"Arquivos deletados imediatamente:           {_totalFilesDeleted}");
            Console.WriteLine($"Pastas deletadas imediatamente:             {_totalDirsDeleted}");
            Console.WriteLine($"Arquivos agendados para delete no reboot:   {_totalFilesScheduledForDelete}");
            Console.WriteLine($"Pastas agendadas para delete no reboot:     {_totalDirsScheduledForDelete}");
            Console.WriteLine($"Tempo total:                                 {stopwatch.Elapsed}");

            if (_errors.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Ocorreram alguns erros (normal por permissão/arquivo em uso):");
                Console.ResetColor();

                foreach (var err in _errors.Take(50))
                    Console.WriteLine("- " + err);

                if (_errors.Count > 50)
                    Console.WriteLine($"... (+{_errors.Count - 50} erros adicionais)");
            }

            Console.WriteLine();
            Console.WriteLine("Pressione qualquer tecla para sair...");
            Console.ReadKey();
        }

        #region Limpeza de Temporários do Windows

        private static void CleanTempFolders()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== LIMPANDO PASTAS TEMPORÁRIAS ===");
            Console.ResetColor();

            var tempPaths = new List<string>();

            try
            {
                var userTemp = Path.GetTempPath();
                if (!string.IsNullOrWhiteSpace(userTemp))
                    tempPaths.Add(userTemp);
            }
            catch (Exception ex)
            {
                LogError("Erro ao obter Path.GetTempPath()", ex);
            }

            // C:\Windows\Temp
            var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            tempPaths.Add(windowsTemp);

            // C:\Users\...\AppData\Local\Temp
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    var localTemp = Path.Combine(localAppData, "Temp");
                    tempPaths.Add(localTemp);
                }
            }
            catch (Exception ex)
            {
                LogError("Erro ao montar pasta Temp em LocalApplicationData", ex);
            }

            tempPaths = tempPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in tempPaths)
            {
                CleanDirectoryContents(path, "Temporários");
            }
        }

        #endregion

        #region Limpeza de Caches dos Navegadores

        private static void CleanBrowserCaches()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== LIMPANDO CACHES DE NAVEGADORES ===");
            Console.ResetColor();

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (string.IsNullOrWhiteSpace(localAppData) && string.IsNullOrWhiteSpace(roamingAppData))
            {
                Console.WriteLine("Não foi possível obter AppData. Pulando limpeza de navegadores.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                CleanChromiumBrowser("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"));
                CleanChromiumBrowser("Microsoft Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
                CleanChromiumBrowser("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"));
                CleanChromiumBrowser("Opera", Path.Combine(localAppData, "Opera Software", "Opera Stable"));
                CleanChromiumBrowser("Opera GX", Path.Combine(localAppData, "Opera Software", "Opera GX Stable"));
                CleanChromiumBrowser("Vivaldi", Path.Combine(localAppData, "Vivaldi", "User Data"));
            }

            CleanFirefoxCaches(localAppData, roamingAppData);
        }

        private static void CleanChromiumBrowser(string browserName, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            {
                Console.WriteLine($"[{browserName}] Pasta base não encontrada, pulando: {basePath}");
                return;
            }

            Console.WriteLine($"\n[{browserName}] Limpando caches em: {basePath}");

            try
            {
                var profileDirs = new List<string>();

                if (Directory.Exists(Path.Combine(basePath, "Default")) ||
                    Directory.Exists(Path.Combine(basePath, "Profile 1")))
                {
                    profileDirs.AddRange(Directory.EnumerateDirectories(basePath));
                }
                else
                {
                    profileDirs.Add(basePath);
                }

                foreach (var prof in profileDirs.Distinct())
                {
                    CleanChromiumProfile(browserName, prof);
                }
            }
            catch (Exception ex)
            {
                LogError($"Erro ao varrer perfis do navegador {browserName} [{basePath}]", ex);
            }
        }

        private static void CleanChromiumProfile(string browserName, string profilePath)
        {
            var cacheFolders = new[]
            {
                "Cache",
                "Code Cache",
                "GPUCache",
                "ShaderCache",
                "Service Worker\\CacheStorage",
                "Service Worker\\ScriptCache",
                "Network\\Service Worker",
                "GrShaderCache"
            };

            foreach (var relative in cacheFolders)
            {
                var fullPath = Path.Combine(profilePath, relative);
                CleanDirectoryContents(fullPath, $"{browserName} - Cache");
            }
        }

        private static void CleanFirefoxCaches(string? localAppData, string? roamingAppData)
        {
            Console.WriteLine("\n[Firefox] Limpando caches...");

            var profilesRoot = string.Empty;
            if (!string.IsNullOrWhiteSpace(roamingAppData))
            {
                profilesRoot = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
            }

            if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
            {
                Console.WriteLine("[Firefox] Nenhum perfil encontrado. Pulando.");
                return;
            }

            try
            {
                var profileDirs = Directory.EnumerateDirectories(profilesRoot).ToList();
                if (!profileDirs.Any())
                {
                    Console.WriteLine("[Firefox] Nenhum perfil encontrado. Pulando.");
                    return;
                }

                foreach (var profileDir in profileDirs)
                {
                    var profileName = Path.GetFileName(profileDir) ?? profileDir;

                    if (!string.IsNullOrWhiteSpace(localAppData))
                    {
                        var localProfile = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles", profileName);

                        CleanDirectoryContents(
                            Path.Combine(localProfile, "cache2"),
                            "Firefox - cache2");

                        CleanDirectoryContents(
                            Path.Combine(localProfile, "startupCache"),
                            "Firefox - startupCache");

                        CleanDirectoryContents(
                            Path.Combine(localProfile, "shader-cache"),
                            "Firefox - shader-cache");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Erro ao limpar cache do Firefox", ex);
            }
        }

        #endregion

        #region Limpeza básica de logs do Windows

        private static void CleanWindowsLogsBasic()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== LIMPANDO ALGUNS LOGS/CACHES BÁSICOS DO WINDOWS ===");
            Console.ResetColor();

            try
            {
                var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                var basicLogsFolders = new[]
                {
                    Path.Combine(windowsFolder, "SoftwareDistribution", "Download"), // Windows Update cache
                    Path.Combine(windowsFolder, "Logs"),                            // inclui Logs\CBS
                    Path.Combine(windowsFolder, "Prefetch")
                };

                foreach (var folder in basicLogsFolders)
                {
                    CleanDirectoryContents(folder, "Windows Logs/Cache");
                }
            }
            catch (Exception ex)
            {
                LogError("Erro ao limpar logs/caches básicos do Windows", ex);
            }
        }

        #endregion

        #region Limpeza extra de caches do Windows (DeliveryOptimization, WER, Explorer, etc.)

        private static void CleanWindowsCacheFolders()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== LIMPANDO PASTAS DE CACHE ADICIONAIS DO WINDOWS ===");
            Console.ResetColor();

            try
            {
                var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                var extraFolders = new List<string>
                {
                    // Delivery Optimization
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Windows", "DeliveryOptimization"),

                    // WER (Windows Error Reporting)
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Windows", "WER"),

                    // Diagnosis
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Diagnosis"),

                    // Cache de impressão
                    Path.Combine(windowsFolder, "System32", "spool", "PRINTERS"),

                    // Mini dumps (BSOD / falhas)
                    Path.Combine(windowsFolder, "Minidump")
                };

                // Cache de miniaturas do Explorer
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    extraFolders.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"));
                }

                foreach (var folder in extraFolders)
                {
                    CleanDirectoryContents(folder, "Windows Cache Extra");
                }
            }
            catch (Exception ex)
            {
                LogError("Erro ao limpar caches adicionais do Windows", ex);
            }
        }

        #endregion

        #region Lixeira do Windows

        private static void EmptyRecycleBin()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== LIMPANDO LIXEIRA DO WINDOWS ===");
            Console.ResetColor();

            try
            {
                // null -> todas as unidades
                var result = SHEmptyRecycleBin(
                    IntPtr.Zero,
                    null,
                    RecycleFlags.SHERB_NOCONFIRMATION |
                    RecycleFlags.SHERB_NOPROGRESSUI |
                    RecycleFlags.SHERB_NOSOUND);

                if (result != 0)
                {
                    _errors.Add($"Falha ao esvaziar Lixeira. Código de retorno: {result}");
                    Console.WriteLine($"[AVISO] Não foi possível esvaziar a Lixeira. Código={result}");
                }
                else
                {
                    Console.WriteLine("Lixeira esvaziada com sucesso.");
                }
            }
            catch (Exception ex)
            {
                LogError("Erro ao esvaziar Lixeira do Windows", ex);
            }
        }

        #endregion

        #region Utilitários de Limpeza

        private static void CleanDirectoryContents(string path, string? context = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!Directory.Exists(path))
                return;

            Console.WriteLine($"[LIMPANDO] ({context}) {path}");

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                {
                    DeleteFileSafe(file);
                }

                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
                {
                    DeleteDirectorySafe(dir);
                }
            }
            catch (Exception ex)
            {
                LogError($"Erro ao limpar conteúdo da pasta: {path}", ex);
            }
        }

        private static void DeleteFileSafe(string file)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists)
                    return;

                fileInfo.IsReadOnly = false;
                File.SetAttributes(file, FileAttributes.Normal);

                File.Delete(file);
                _totalFilesDeleted++;
            }
            catch (IOException ex)
            {
                if (ScheduleDeleteOnReboot(file, isDirectory: false))
                {
                    Console.WriteLine($"[FORCE] Arquivo em uso agendado para exclusão no reboot: {file}");
                    _totalFilesScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao deletar arquivo (em uso): {file}", ex, logToConsole: false);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (ScheduleDeleteOnReboot(file, isDirectory: false))
                {
                    Console.WriteLine($"[FORCE] Arquivo sem permissão agendado para exclusão no reboot: {file}");
                    _totalFilesScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao deletar arquivo (sem permissão): {file}", ex, logToConsole: false);
                }
            }
            catch (Exception ex)
            {
                LogError($"Erro ao deletar arquivo: {file}", ex, logToConsole: false);
            }
        }

        private static void DeleteDirectorySafe(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    DeleteFileSafe(file);
                }

                foreach (var subDir in Directory
                             .EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    TryDeleteEmptyDirectory(subDir);
                }

                TryDeleteEmptyDirectory(dir);
            }
            catch (IOException ex)
            {
                if (ScheduleDeleteOnReboot(dir, isDirectory: true))
                {
                    Console.WriteLine($"[FORCE] Pasta em uso agendada para exclusão no reboot: {dir}");
                    _totalDirsScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao deletar pasta (em uso): {dir}", ex, logToConsole: false);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (ScheduleDeleteOnReboot(dir, isDirectory: true))
                {
                    Console.WriteLine($"[FORCE] Pasta sem permissão agendada para exclusão no reboot: {dir}");
                    _totalDirsScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao deletar pasta (sem permissão): {dir}", ex, logToConsole: false);
                }
            }
            catch (Exception ex)
            {
                LogError($"Erro ao deletar pasta: {dir}", ex, logToConsole: false);
            }
        }

        private static void TryDeleteEmptyDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    return;

                Directory.Delete(dir, false);
                _totalDirsDeleted++;
            }
            catch (IOException ex)
            {
                if (ScheduleDeleteOnReboot(dir, isDirectory: true))
                {
                    Console.WriteLine($"[FORCE] Pasta vazia em uso agendada para exclusão no reboot: {dir}");
                    _totalDirsScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao tentar deletar pasta vazia: {dir}", ex, logToConsole: false);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (ScheduleDeleteOnReboot(dir, isDirectory: true))
                {
                    Console.WriteLine($"[FORCE] Pasta vazia sem permissão agendada para exclusão no reboot: {dir}");
                    _totalDirsScheduledForDelete++;
                }
                else
                {
                    LogError($"Erro ao tentar deletar pasta vazia (sem permissão): {dir}", ex, logToConsole: false);
                }
            }
            catch (Exception ex)
            {
                LogError($"Erro ao tentar deletar pasta vazia: {dir}", ex, logToConsole: false);
            }
        }

        /// <summary>
        /// FORCE: usa MoveFileEx com MOVEFILE_DELAY_UNTIL_REBOOT
        /// para agendar deleção no próximo reboot.
        /// </summary>
        private static bool ScheduleDeleteOnReboot(string path, bool isDirectory)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return false;

                var ok = MoveFileEx(path, null, MoveFileExFlags.MOVEFILE_DELAY_UNTIL_REBOOT);

                if (!ok)
                {
                    var code = Marshal.GetLastWin32Error();
                    _errors.Add($"Falha ao agendar exclusão no reboot para '{path}'. Win32Error={code}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogError($"Exceção ao agendar exclusão no reboot para: {path}", ex, logToConsole: false);
                return false;
            }
        }

        #endregion

        #region Log de Erros

        private static void LogError(string message, Exception ex, bool logToConsole = true)
        {
            var full = $"{message}: {ex.GetType().Name} - {ex.Message}";
            _errors.Add(full);

            if (logToConsole)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[ERRO] " + full);
                Console.ResetColor();
            }
        }

        #endregion
    }
}

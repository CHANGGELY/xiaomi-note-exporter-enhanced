using OpenQA.Selenium.Chrome;
using OpenQA.Selenium;

namespace xiaomiNoteExporter;

public class Driver
{
    private readonly string[] _args;
    private readonly string _baseDir;

    public Driver(string[] args, string? baseDir = null)
    {
        _args = args ?? Array.Empty<string>();
        _baseDir = string.IsNullOrWhiteSpace(baseDir)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(baseDir);
    }

    public ChromeDriver Prepare(bool useStaticDriver = false) 
    {
        Directory.CreateDirectory(_baseDir);
        var profileDir = ResolveProfileDir();
        Directory.CreateDirectory(profileDir);
        CleanupStaleChromeState(profileDir);

        if (useStaticDriver)
        {
            var chromeDriverExe = TryFindChromeDriverExePath();
            if (string.IsNullOrWhiteSpace(chromeDriverExe))
            {
                throw new FileNotFoundException("chromedriver.exe not found. Put it next to the exe, add it to PATH, or keep it under 导出结果\\工具.");
            }

            var staticService = ChromeDriverService.CreateDefaultService(
                Path.GetDirectoryName(chromeDriverExe)!,
                Path.GetFileName(chromeDriverExe)
                );
            ConfigureService(staticService);
            try
            {
                return StartWithFallback(staticService, profileDir);
            }
            catch
            {
                TryDisposeService(staticService);
                throw;
            }
        }

        var service = ChromeDriverService.CreateDefaultService();
        ConfigureService(service);

        try
        {
            return StartWithFallback(service, profileDir);
        }
        catch (Exception ex) when (ex is WebDriverException || ex is InvalidOperationException)
        {
            TryDisposeService(service);

            var chromeDriverExe = TryFindChromeDriverExePath();
            if (string.IsNullOrWhiteSpace(chromeDriverExe))
            {
                throw new WebDriverException(
                    "Unable to start ChromeDriver via Selenium Manager, and chromedriver.exe was not found locally.",
                    ex
                    );
            }

            var staticService = ChromeDriverService.CreateDefaultService(
                Path.GetDirectoryName(chromeDriverExe)!,
                Path.GetFileName(chromeDriverExe)
                );
            ConfigureService(staticService);
            try
            {
                return StartWithFallback(staticService, profileDir);
            }
            catch
            {
                TryDisposeService(staticService);
                throw;
            }
        }
    }

    private void ConfigureService(ChromeDriverService service)
    {
        service.HideCommandPromptWindow = true;

        var logEnabled = Environment.GetEnvironmentVariable("XNE_CHROMEDRIVER_LOG");
        if (string.IsNullOrWhiteSpace(logEnabled) || string.Equals(logEnabled, "0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_baseDir);
            service.EnableVerboseLogging = true;
            service.EnableAppendLog = true;
            service.LogPath = Path.Combine(_baseDir, "chromedriver.log");
        }
        catch
        {
            // Ignore logging errors - logging is best-effort.
        }
    }

    private ChromeDriver StartWithFallback(ChromeDriverService service, string profileDir)
    {
        try
        {
            return StartWithOptions(service, profileDir, safeMode: false);
        }
        catch (Exception ex) when (ex is WebDriverException || ex is InvalidOperationException)
        {
            if (!LooksLikeChromeStartCrash(ex))
            {
                throw;
            }

            return StartWithOptions(service, profileDir, safeMode: true);
        }
    }

    private ChromeDriver StartWithOptions(ChromeDriverService service, string profileDir, bool safeMode)
    {
        var options = CreateOptions(profileDir, safeMode);
        return TryStartWithProfileRecovery(
            () => new ChromeDriver(service, options),
            profileDir,
            onProfileDirChanged: newProfileDir => options = CreateOptions(newProfileDir, safeMode)
            );
    }

    private ChromeOptions CreateOptions(string profileDir, bool safeMode = false)
    {
        var options = new ChromeOptions();
        options.AddArguments(_args);
        options.AddArgument($@"--user-data-dir={profileDir}");
        options.AddArgument("--profile-directory=Default");

        var headless = Environment.GetEnvironmentVariable("XNE_HEADLESS");
        if (!string.IsNullOrWhiteSpace(headless) && !string.Equals(headless, "0", StringComparison.OrdinalIgnoreCase))
        {
            options.AddArgument("--headless=new");
            options.AddArgument("--window-size=1400,900");
            options.AddArgument("--disable-gpu");
        }

        if (safeMode)
        {
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-software-rasterizer");
            options.AddArgument("--remote-debugging-pipe");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--disable-extensions");
        }

        return options;
    }

    private static void CleanupStaleChromeState(string profileDir)
    {
        if (string.IsNullOrWhiteSpace(profileDir))
        {
            return;
        }

        TryDeleteFile(Path.Combine(profileDir, "DevToolsActivePort"));
        TryDeleteFile(Path.Combine(profileDir, "SingletonLock"));
        TryDeleteFile(Path.Combine(profileDir, "SingletonCookie"));
        TryDeleteFile(Path.Combine(profileDir, "SingletonSocket"));
        TryDeleteFile(Path.Combine(profileDir, "lockfile"));
        TryDeleteFile(Path.Combine(profileDir, "Default", "lockfile"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDisposeService(ChromeDriverService service)
    {
        try
        {
            service.Dispose();
        }
        catch
        {
        }
    }

    private string ResolveProfileDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("XNE_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.GetFullPath(overrideDir);
        }

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xiaomiNoteExporter"
            );

        try
        {
            var overrideFile = Path.Combine(baseDir, "profile_dir.txt");
            if (File.Exists(overrideFile))
            {
                var persisted = File.ReadAllText(overrideFile).Trim();
                if (!string.IsNullOrWhiteSpace(persisted))
                {
                    return Path.GetFullPath(persisted);
                }
            }
        }
        catch
        {
        }

        return Path.Combine(baseDir, "chrome_user_data");
    }

    private static ChromeDriver TryStartWithProfileRecovery(
        Func<ChromeDriver> start,
        string profileDir,
        Action<string> onProfileDirChanged
        )
    {
        try
        {
            return start();
        }
        catch (Exception ex) when (LooksLikeProfileDirProblem(ex))
        {
            var repaired = TryRepairProfileDir(profileDir);
            if (string.IsNullOrWhiteSpace(repaired))
            {
                throw;
            }

            onProfileDirChanged(repaired);
            return start();
        }
    }

    private static bool LooksLikeProfileDirProblem(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Chrome failed to start", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Timed out receiving message from renderer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("user data directory is already in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("profile", StringComparison.OrdinalIgnoreCase) && message.Contains("in use", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeChromeStartCrash(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Chrome failed to start", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Timed out receiving message from renderer", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryRepairProfileDir(string profileDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir))
            {
                return null;
            }

            // If Chrome crashes during startup, the existing profile dir can be left in a broken state.
            // Prefer an atomic rename so the original data is preserved.
            var parent = Path.GetDirectoryName(profileDir);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return null;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupDir = Path.Combine(parent, $"chrome_user_data_bad_{timestamp}");
            try
            {
                Directory.Move(profileDir, backupDir);
                Directory.CreateDirectory(profileDir);
                return profileDir;
            }
            catch
            {
                var fallbackDir = Path.Combine(parent, $"chrome_user_data_recovered_{timestamp}");
                Directory.CreateDirectory(fallbackDir);
                TryPersistProfileOverride(parent, fallbackDir);
                return fallbackDir;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void TryPersistProfileOverride(string parentDir, string profileDir)
    {
        try
        {
            File.WriteAllText(Path.Combine(parentDir, "profile_dir.txt"), profileDir);
        }
        catch
        {
        }
    }

    private static string? TryFindChromeDriverExePath()
    {
        var directCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "chromedriver.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "chromedriver.exe")
        };

        foreach (var candidate in directCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(startDir);
            for (int i = 0; i < 10 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "chromedriver.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var underExportTools = Path.Combine(dir.FullName, "导出结果", "工具", "chromedriver.exe");
                if (File.Exists(underExportTools))
                {
                    return underExportTools;
                }

                dir = dir.Parent;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathEnv.Split(Path.PathSeparator))
        {
            var dir = segment.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, "chromedriver.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

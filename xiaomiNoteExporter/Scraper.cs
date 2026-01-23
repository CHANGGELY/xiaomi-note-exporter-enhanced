using System.Drawing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Pastel;

using xiaomiNoteExporter.Extensions;

namespace xiaomiNoteExporter;

public partial class Scraper(ChromeDriver driver, Action shutdownHandler, string? baseDir = null)
{
    private readonly ChromeDriver _driver = driver;

    private readonly string _baseDir = string.IsNullOrWhiteSpace(baseDir)
        ? AppDomain.CurrentDomain.BaseDirectory
        : Path.GetFullPath(baseDir);

    private WebDriverWait? _wait;

    private readonly Action _shutdownHandler = shutdownHandler;

    private int totalNotes = 0;

    private int currentNote = 0;

    /// <summary>
    /// Method that starts the scraping process.
    /// </summary>
    /// <param name="domain">Domain address to be visited by <c>ChromeDriver</c>.</param>
    /// <param name="timeStampFormat">Format of the timestamp for file (or directory) name.</param>
    /// <param name="split">If <c>true</c> then notes will be split as separate files.</param>
    public void Start(string domain, string timeStampFormat, bool split = false, bool exportImages = true)
    {
        Directory.CreateDirectory(_baseDir);
        _wait = _driver.GetWait(TimeSpan.FromSeconds(10));

        _driver.Navigate().GoToUrl($"https://{domain}/note/h5/?_locale=en-US");
        _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);

        try
        {
            Console.WriteLine($"\n{"[INFO]".Pastel(Color.Cyan)} Waiting for login... (Auto-detecting)");
            var loginWait = _driver.GetWait(TimeSpan.FromMinutes(10));
            loginWait.Until(e => e.FindElements(By.XPath(@"//button[contains(@class, 'btn-create')]")).Count > 0);
            Console.WriteLine($"{"[SUCCESS]".Pastel(Color.Green)} Login detected!");
        }
        catch (WebDriverTimeoutException)
        {
            _shutdownHandler();
            Console.WriteLine($"\n{"User didn't sign into Mi Cloud or account is invalid.".Pastel(Color.Red)}");
            Console.WriteLine("Application will exit now...".Pastel(Color.Gray));
            Environment.Exit(0);
        }

        Scrape(timeStampFormat, domain, split, exportImages);
    }

    private void Scrape(string timeStampFormat, string domain, bool split, bool exportImages)
    {
        if (_wait is null)
        {
            return;
        }

        // After login, the app bootstrap can take longer than the default 10s wait.
        WaitForSpinnerIdle(TimeSpan.FromMinutes(2));
        _driver.GetWait(TimeSpan.FromMinutes(2))
            .Until(e => e.FindElement(By.XPath(@"//button[contains(@class, 'btn-create')]")).Displayed);
        TryConsoleClear();

        ExportManifest? manifest = null;
        string? manifestWritePath = null;
        try
        {
            Stopwatch watch = new();
            var innerWait = _driver.GetWait(TimeSpan.FromMilliseconds(10));
            watch.Start();

            IWebElement noteCountElement = _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'note-count-select')]")));
            string notesElements = noteCountElement.Text;
            totalNotes = int.Parse(DigitRegex().Replace(notesElements, ""));

            IWebElement notesList = FindNotesList(noteCountElement);

            string currentExportDate = DateTime.Now.ToString(timeStampFormat);
            string exportName = $"exported_notes_{currentExportDate}";

            if (split)
            {
                var rootDir = _baseDir;
                var existingExportDir = Directory
                    .EnumerateDirectories(rootDir, "exported_notes_*", SearchOption.TopDirectoryOnly)
                    .Select(d => new
                    {
                        Dir = d,
                        Count = Directory.EnumerateFiles(d, "note_*", SearchOption.TopDirectoryOnly).Count(),
                        LastWrite = Directory.GetLastWriteTimeUtc(d)
                    })
                    .Where(x => x.Count > 0)
                    .OrderByDescending(x => x.Count)
                    .ThenByDescending(x => x.LastWrite)
                    .Select(x => x.Dir)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(existingExportDir))
                {
                    exportName = Path.GetFileName(existingExportDir);
                }

                Directory.CreateDirectory(Path.Combine(rootDir, exportName));
            }

            string fileName = $"{exportName}.md"; // file name for accumulated notes (without split)

            string imgDir = Path.Combine(
                _baseDir,
                split ? $"{exportName}\\images" : $"images_{currentExportDate}"
                );

            // create directory for exported images (if enabled)
            if (exportImages)
            {
                Directory.CreateDirectory(imgDir);
            }

            var baseDir = _baseDir;
            var exportDir = Path.Combine(_baseDir, exportName);
            manifestWritePath = split
                ? Path.Combine(exportDir, "manifest.txt")
                : Path.Combine(baseDir, $"{exportName}.manifest.txt");
            var manifestLegacyPath = split
                ? Path.Combine(exportDir, "manifest.json")
                : Path.Combine(baseDir, $"{exportName}.manifest.json");
            var manifestReadPath = File.Exists(manifestWritePath)
                ? manifestWritePath
                : (File.Exists(manifestLegacyPath) ? manifestLegacyPath : manifestWritePath);
            manifest = ExportManifest.Load(
                manifestReadPath,
                split ? exportDir : Path.Combine(baseDir, fileName),
                split
                );
            if (totalNotes > 0)
            {
                manifest.CloudTotalNotes = totalNotes;
            }

            var progressFilePath = Path.Combine(split ? exportDir : baseDir, "progress.txt");
            try
            {
                var targetPath = split ? exportDir : Path.Combine(baseDir, fileName);
                Console.WriteLine($"\n{"[INFO]".Pastel(Color.Cyan)} Export target: {targetPath}");
                Console.WriteLine($"{"[INFO]".Pastel(Color.Cyan)} Progress file: {progressFilePath}");
            }
            catch
            {
            }
            TryWriteProgressFile(progressFilePath, manifest.Entries.Count, totalNotes);

            int listIndex = 0;
            int noGrowth = 0;
            int lastListCount = -1;
            var lastGrowthUtc = DateTime.UtcNow;
            var lastProgressUtc = DateTime.UtcNow;
            var lastHeartbeatUtc = DateTime.UtcNow;
            var lastProgressSignature = string.Empty;
            var lastListResolveUtc = DateTime.UtcNow;
            var stallTimeout = TimeSpan.FromMinutes(GetEnvInt("XNE_STALL_MINUTES", 15));
            var heartbeatInterval = TimeSpan.FromSeconds(GetEnvInt("XNE_HEARTBEAT_SECONDS", 60));
            bool completed = false;
            string? stopReason = null;

            int refreshAttempts = 0;
            long lastScrollHeight = -1;

            int exportedNew = 0;
            int skippedAsDuplicate = 0;
            int unsupportedNew = 0;
            var lastManifestFlushUtc = DateTime.UtcNow;
            int lastProgressLineLength = 0;

            while (true)
            {
                var processedUnique = manifest.Entries.Count;
                var progressBar = string.Empty;
                try
                {
                    if (!Console.IsOutputRedirected && totalNotes > 0)
                    {
                        progressBar = RenderProgressBar(processedUnique, totalNotes) + " ";
                    }
                }
                catch
                {
                }

                string progress = totalNotes > 0
                    ? $"{progressBar}Parsed {processedUnique}/{totalNotes} ({processedUnique.GetPercentage(totalNotes)}%)"
                    : $"Parsed {processedUnique} notes...";

                if (progress.Length < lastProgressLineLength)
                {
                    progress = progress + new string(' ', lastProgressLineLength - progress.Length);
                }
                lastProgressLineLength = progress.Length;

                TrySetConsoleTitle(progress);
                Console.Write($"\r{progress}");

                var progressSignature = $"{processedUnique}|{currentNote}|{listIndex}|{lastListCount}|{lastScrollHeight}";
                if (!string.Equals(progressSignature, lastProgressSignature, StringComparison.Ordinal))
                {
                    lastProgressSignature = progressSignature;
                    lastProgressUtc = DateTime.UtcNow;
                }

                var spinnerVisible = IsSpinnerVisible();

                if (DateTime.UtcNow - lastHeartbeatUtc >= heartbeatInterval)
                {
                    Console.WriteLine($"\n{"[HEARTBEAT]".Pastel(Color.DimGray)} {DateTime.Now:HH:mm:ss} processed={processedUnique} listIndex={listIndex} listCount={lastListCount} spinner={spinnerVisible}");
                    TryWriteProgressFile(progressFilePath, processedUnique, totalNotes);
                    lastHeartbeatUtc = DateTime.UtcNow;
                }

                if (DateTime.UtcNow - lastProgressUtc >= stallTimeout)
                {
                    stopReason = $"No progress for {stallTimeout.TotalMinutes:0} minutes. Stopping to avoid hang.";
                    watch.Stop();
                    _shutdownHandler();
                    break;
                }

                var listItems = GetNoteListItems(ref notesList);
                if (listItems.Count <= 1 && !spinnerVisible && DateTime.UtcNow - lastListResolveUtc > TimeSpan.FromSeconds(20))
                {
                    lastListResolveUtc = DateTime.UtcNow;
                    try
                    {
                        noteCountElement = _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'note-count-select')]")));
                        var refreshedList = FindNotesList(noteCountElement);
                        if (!ReferenceEquals(refreshedList, notesList))
                        {
                            notesList = refreshedList;
                            listIndex = 0;
                            noGrowth = 0;
                            lastListCount = -1;
                            lastGrowthUtc = DateTime.UtcNow;
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }

                try
                {
                    var js = (IJavaScriptExecutor)_driver;
                    var scrollHeight = Convert.ToInt64(js.ExecuteScript("return arguments[0].scrollHeight;", notesList));
                    if (scrollHeight != lastScrollHeight)
                    {
                        lastScrollHeight = scrollHeight;
                        lastGrowthUtc = DateTime.UtcNow;
                    }
                }
                catch
                {
                }

                if (listItems.Count == lastListCount)
                {
                    if (!spinnerVisible)
                    {
                        noGrowth++;
                    }
                }
                else
                {
                    noGrowth = 0;
                    lastListCount = listItems.Count;
                    lastGrowthUtc = DateTime.UtcNow;
                }

                if (spinnerVisible)
                {
                    noGrowth = 0;
                    lastGrowthUtc = DateTime.UtcNow;
                }

                if (totalNotes > 0 && processedUnique >= totalNotes)
                {
                    watch.Stop();
                    _shutdownHandler();
                    Process.Start("explorer.exe", _baseDir + "");
                    completed = true;
                    break;
                }

                if (listIndex >= listItems.Count)
                {
                    var windowBefore = GetListWindowSignature(listItems);

                    if (IsAtListBottom(notesList) && noGrowth > 40)
                    {
                        if (totalNotes > 0 && processedUnique < totalNotes)
                        {
                            try
                            {
                                notesList.SendKeys(Keys.End);
                            }
                            catch
                            {
                            }

                            try
                            {
                                var js = (IJavaScriptExecutor)_driver;
                                js.ExecuteScript("arguments[0].scrollTop = arguments[0].scrollHeight;", notesList);
                                Thread.Sleep(200);
                                js.ExecuteScript("arguments[0].scrollBy(0, 2000);", notesList);
                                js.ExecuteScript("arguments[0].dispatchEvent(new Event('scroll', { bubbles: true }));", notesList);
                                js.ExecuteScript("arguments[0].dispatchEvent(new WheelEvent('wheel', { deltaY: 2500, bubbles: true }));", notesList);
                            }
                            catch
                            {
                            }

                            WaitForSpinnerIdle(TimeSpan.FromSeconds(20));

                            var newListItems = GetNoteListItems(ref notesList);
                            if (newListItems.Count > listItems.Count)
                            {
                                noGrowth = 0;
                                lastListCount = newListItems.Count;
                                lastGrowthUtc = DateTime.UtcNow;
                                continue;
                            }

                            var windowAfterLoadAttempt = GetListWindowSignature(newListItems);
                            if (windowAfterLoadAttempt != windowBefore)
                            {
                                listIndex = 0;
                                noGrowth = 0;
                                lastListCount = newListItems.Count;
                                lastGrowthUtc = DateTime.UtcNow;
                                continue;
                            }

                            if (DateTime.UtcNow - lastGrowthUtc > TimeSpan.FromMinutes(10))
                            {
                                watch.Stop();
                                _shutdownHandler();
                                Process.Start("explorer.exe", _baseDir + "");
                                stopReason ??= "No list growth for 10 minutes at list bottom.";
                                break;
                            }

                            if (DateTime.UtcNow - lastGrowthUtc > TimeSpan.FromMinutes(2) && refreshAttempts < 3)
                            {
                                refreshAttempts++;
                                _driver.Navigate().Refresh();
                                _wait.Until(e => e.FindElement(By.XPath(@"//button[contains(@class, 'btn-create')]")).Displayed);
                                noteCountElement = _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'note-count-select')]")));
                                notesList = FindNotesList(noteCountElement);
                                listIndex = 0;
                                noGrowth = 0;
                                lastListCount = -1;
                                lastScrollHeight = -1;
                                lastGrowthUtc = DateTime.UtcNow;
                                Thread.Sleep(800);
                            }

                            continue;
                        }

                        watch.Stop();
                        _shutdownHandler();
                        Process.Start("explorer.exe", _baseDir + "");
                        stopReason ??= "Reached list bottom with no further growth.";
                        break;
                    }

                    try
                    {
                        var js = (IJavaScriptExecutor)_driver;
                        js.ExecuteScript("arguments[0].scrollBy(0, 1200);", notesList);
                    }
                    catch
                    {
                    }

                    Thread.Sleep(350);

                    var newListItemsAfterScroll = GetNoteListItems(ref notesList);
                    var windowAfterScroll = GetListWindowSignature(newListItemsAfterScroll);
                    if (windowAfterScroll != windowBefore)
                    {
                        listIndex = 0;
                        noGrowth = 0;
                        lastListCount = newListItemsAfterScroll.Count;
                        lastGrowthUtc = DateTime.UtcNow;
                    }
                    continue;
                }

                var element = listItems[listIndex];

                var listItemKey = TryGetListItemKey(element);
                var listItemTextKey = TryGetListItemTextKey(element);
                var stableListItemKey = PickPreferredListKey(listItemKey, listItemTextKey);

                var isDuplicate = false;
                if (!string.IsNullOrWhiteSpace(listItemKey) && listItemKey.StartsWith("mi:", StringComparison.OrdinalIgnoreCase))
                {
                    isDuplicate = manifest.Contains(listItemKey);
                }
                else if (!string.IsNullOrWhiteSpace(listItemKey) && !string.IsNullOrWhiteSpace(listItemTextKey))
                {
                    // Only treat as duplicate when both signals match the manifest to avoid false positives.
                    isDuplicate = manifest.Contains(listItemKey) && manifest.Contains(listItemTextKey);
                }

                if (isDuplicate)
                {
                    ExecuteScroll(notesList, element);
                    currentNote++;
                    listIndex++;
                    skippedAsDuplicate++;
                    continue;
                }

                element.Click();
                Thread.Sleep(200);

                    // creation date text (retrieved from UI)
                    string createdString = element.FindElement(By.XPath(@".//div[2]/div[1]")).Text;

                    // creation date (calculated from retrieved text)
                    GetCreatedDate(createdString, out DateTime createdDate);

                    try
                    {
                        innerWait.Until(e => e.FindElements(By.XPath(@"//div[contains(@class, 'open')]/div[2][not(./i)]")).Count == 1);
                    }
                    catch
                    {
                        var fallbackUnsupportedPayload = $"unsupported:{createdString}\n{element.Text}";
                        var unsupportedKey = stableListItemKey ?? ExportManifest.BuildHashKey(createdDate, string.Empty, fallbackUnsupportedPayload);
                        if (manifest.Contains(unsupportedKey))
                        {
                            ExecuteScroll(notesList, element);
                            currentNote++;
                            listIndex++;
                            skippedAsDuplicate++;
                            continue;
                        }

                        var targetFile = !split
                            ? fileName
                            : GetSplitNoteRelativePath(exportName, timeStampFormat, createdDate, unsupportedKey);

                        // found note that is not supported, log this fact and continue
                        SaveToFile(
                            targetFile,
                            $"**Unsupported note type (Mind-map or Sound note) (Created at: {createdDate:dd/MM/yyyy HH:mm})**",
                            createdDate,
                            noteKey: unsupportedKey
                            );

                        manifest.Upsert(unsupportedKey, "unsupported", createdDate, null);
                        if (DateTime.UtcNow - lastManifestFlushUtc > TimeSpan.FromSeconds(2))
                        {
                            manifest.Save(manifestWritePath!);
                            lastManifestFlushUtc = DateTime.UtcNow;
                        }

                        ExecuteScroll(notesList, element);
                        currentNote++;
                        listIndex++;
                        unsupportedNew++;
                        continue;
                    }

                    _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'origin-title')]/div")).Displayed);

                    var noteContainer = _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'pm-container')]")));

                    string title = _wait.Until(e => e.FindElement(By.XPath(@"//div[contains(@class, 'origin-title')]/div"))).Text;
                    string value = noteContainer.Text;

                    var openedNoteKey = TryGetOpenedNoteKey(noteContainer);
                    var noteKey = openedNoteKey ?? stableListItemKey ?? ExportManifest.BuildHashKey(createdDate, title, value);
                    if (manifest.Contains(noteKey))
                    {
                        ExecuteScroll(notesList, element);
                        currentNote++;
                        listIndex++;
                        skippedAsDuplicate++;
                        continue;
                    }

                    var noteTargetFile = !split
                        ? fileName
                        : GetSplitNoteRelativePath(exportName, timeStampFormat, createdDate, noteKey);

                    SaveToFile(
                        noteTargetFile,
                        value,
                        createdDate,
                        title,
                        noteKey
                        );

                    manifest.Upsert(noteKey, "exported", createdDate, title);
                    exportedNew++;
                    if (DateTime.UtcNow - lastManifestFlushUtc > TimeSpan.FromSeconds(2))
                    {
                        manifest.Save(manifestWritePath!);
                        lastManifestFlushUtc = DateTime.UtcNow;
                    }

                    if (!exportImages)
                    {
                        // skip image export if user chose so
                        ExecuteScroll(notesList, element);
                        currentNote++;
                        listIndex++;
                        continue;
                    }

                    var initialImgs = DriverHelpers.TryFindImages(noteContainer);

                    if (initialImgs.Count > 0)
                    {
                        DriverHelpers.WaitUntilImagesAreRealAndLoaded(_driver, initialImgs, TimeSpan.FromSeconds(3));

                        var embeddedImages = noteContainer.FindElements(By.CssSelector(".image-view img"));

                        if (embeddedImages.Count != 0)
                        {
                            var cookies = _driver.Manage().Cookies.AllCookies;

                            // IWebElement because non nullish type is needed (force typing)
                            foreach (var t in embeddedImages.Select((item, idx) => (idx, (IWebElement)item)))
                            {
                                int idx = t.idx;
                                IWebElement item = t.Item2;

                                var imgSrc = DriverHelpers.GetCurrentSrc(_driver, item);

                                if (string.IsNullOrWhiteSpace(imgSrc) || imgSrc.Contains("data:"))
                                {
                                    // skip base64 images and empty sources
                                    continue;
                                }

                                string imgName = $"note_img_{idx}_{createdDate.ToString(timeStampFormat)}.png";
                                string imgPath = Path.Combine(imgDir, imgName);

                                SaveImage(imgPath, imgSrc, cookies);
                            }
                        }
                    }

                    ExecuteScroll(notesList, element);
                    currentNote++;
                    listIndex++;
            }

            manifest.Save(manifestWritePath!);

            if (completed)
            {
                TryConsoleClear();
                TrySetConsoleTitle(string.Format(
                    "Completed! (took {0:00}:{1:00}:{2:00})",
                    watch.Elapsed.Hours,
                    watch.Elapsed.Minutes,
                    watch.Elapsed.Seconds
                    ));
            }
            else if (!string.IsNullOrWhiteSpace(stopReason))
            {
                Console.WriteLine($"\n{"[WARN]".Pastel(Color.Yellow)} {stopReason}");
            }

            TryWriteProgressFile(
                progressFilePath,
                manifest.Entries.Count,
                totalNotes,
                completed ? "Completed" : "Stopped",
                stopReason
                );

            if (split)
            {
                var message = completed
                    ? $"Successfully exported notes to {exportName.Pastel(Color.WhiteSmoke)} directory\n".Pastel(Color.LimeGreen)
                    : $"Partial export saved to {exportName.Pastel(Color.WhiteSmoke)} directory\n".Pastel(Color.Yellow);
                Console.WriteLine(message);
            }
            else
            {
                var message = completed
                    ? $"Successfully exported notes to {fileName.Pastel(Color.WhiteSmoke)}\n".Pastel(Color.LimeGreen)
                    : $"Partial export saved to {fileName.Pastel(Color.WhiteSmoke)}\n".Pastel(Color.Yellow);
                Console.WriteLine(message);
            }

            Console.WriteLine("Press any key to close application...".Pastel(Color.Gray));
            Console.WriteLine($"\n{"[SUMMARY]".Pastel(Color.Cyan)} Total in cloud: {totalNotes}, Processed: {currentNote}, New exported: {exportedNew}, New unsupported: {unsupportedNew}, Skipped as duplicate: {skippedAsDuplicate}, Total exported in manifest: {manifest.CountByKind("exported")}, Total unsupported in manifest: {manifest.CountByKind("unsupported")}");
            TryReadKey();
        }
        catch (Exception ex)
        {
            try
            {
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifestWritePath))
                {
                    manifest.Save(manifestWritePath!);
                }
            }
            catch
            {
            }

            _shutdownHandler();
            Console.WriteLine($"\nPlease report this error on GitHub".Pastel(Color.Gray));
            Console.WriteLine($"\n{ex.ToString().Pastel(Color.Red)}");
            TryReadKey();
        }
    }

    private static void TryConsoleClear()
    {
        try
        {
            Console.Clear();
        }
        catch
        {
        }
    }

    private static string RenderProgressBar(int done, int total, int width = 24)
    {
        if (total <= 0)
        {
            return string.Empty;
        }

        if (done < 0)
        {
            done = 0;
        }
        else if (done > total)
        {
            done = total;
        }

        width = Math.Clamp(width, 10, 60);
        var ratio = (double)done / total;
        var filled = (int)Math.Round(ratio * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);

        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static void TryWriteProgressFile(string path, int done, int total, string? status = null, string? message = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var statusLine = string.Empty;
            var messageLine = string.Empty;

            var callerStatus = status;
            if (!string.IsNullOrWhiteSpace(callerStatus))
            {
                statusLine = $"Status: {callerStatus.Trim()}\r\n";
            }

            var callerMessage = message;
            if (!string.IsNullOrWhiteSpace(callerMessage))
            {
                callerMessage = callerMessage.Replace("\r", " ").Replace("\n", " ").Trim();
                messageLine = $"Message: {callerMessage}\r\n";
            }

            var content = total > 0
                ? $"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{statusLine}{messageLine}Done: {done}/{total} ({done.GetPercentage(total)}%)\r\n"
                : $"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{statusLine}{messageLine}Done: {done}\r\n";

            File.WriteAllText(path, content);
        }
        catch
        {
        }
    }

    private static void TrySetConsoleTitle(string title)
    {
        try
        {
            Console.Title = title ?? string.Empty;
        }
        catch
        {
        }
    }

    private static void TryReadKey()
    {
        try
        {
            if (!Console.IsInputRedirected)
            {
                Console.ReadKey();
            }
        }
        catch
        {
        }
    }

    private static int GetEnvInt(string name, int fallback)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (int.TryParse(raw, out var value) && value > 0)
            {
                return value;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private IReadOnlyList<IWebElement> GetNoteListItems(ref IWebElement notesList)
    {
        try
        {
            var items = ResolveNoteListItems(notesList, allowGlobalFallback: true, out var resolvedContainer);
            if (resolvedContainer is not null && !ReferenceEquals(resolvedContainer, notesList))
            {
                notesList = resolvedContainer;
            }
            return items;
        }
        catch (StaleElementReferenceException)
        {
            notesList = FindNotesList();
            var items = ResolveNoteListItems(notesList, allowGlobalFallback: true, out var resolvedContainer);
            if (resolvedContainer is not null && !ReferenceEquals(resolvedContainer, notesList))
            {
                notesList = resolvedContainer;
            }
            return items;
        }
    }

    private IWebElement FindNotesList(IWebElement? anchor = null)
    {
        if (_wait is null)
        {
            return _driver.FindElement(By.XPath("//div[contains(@class, 'note-list-items')]"));
        }

        return _wait.Until(_ => FindNotesListInternal(anchor));
    }

    private IWebElement? FindNotesListInternal(IWebElement? anchor)
    {
        var candidates = new List<IWebElement>();
        candidates.AddRange(_driver.FindElements(By.CssSelector("div.note-list-items,div[class*='note-list'],div[class*='noteList'],div[role='list'],ul[role='list'],ul")));

        if (candidates.Count == 0)
        {
            return null;
        }

        var visibleCandidates = FilterVisibleItems(candidates);
        var best = PickBestListContainer(visibleCandidates, anchor);
        if (best is not null)
        {
            return best;
        }

        return visibleCandidates.Count > 0 ? visibleCandidates[0] : candidates[0];
    }

    private IWebElement? PickBestListContainer(IReadOnlyList<IWebElement> candidates, IWebElement? anchor)
    {
        IWebElement? best = null;
        int bestCount = 0;
        int bestDistance = int.MaxValue;

        var anchorLeft = TryGetElementLeft(anchor);

        foreach (var candidate in candidates)
        {
            var items = ResolveNoteListItems(candidate, allowGlobalFallback: false, out _);
            if (items.Count <= 0)
            {
                continue;
            }

            var candidateLeft = TryGetElementLeft(candidate);
            var distance = (anchorLeft.HasValue && candidateLeft.HasValue)
                ? Math.Abs(candidateLeft.Value - anchorLeft.Value)
                : int.MaxValue;

            if (items.Count > bestCount || (items.Count == bestCount && distance < bestDistance))
            {
                best = candidate;
                bestCount = items.Count;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int? TryGetElementLeft(IWebElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Location.X;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<IWebElement> ResolveNoteListItems(IWebElement notesList, bool allowGlobalFallback, out IWebElement? resolvedContainer)
    {
        resolvedContainer = notesList;

        IReadOnlyList<IWebElement> best = Array.Empty<IWebElement>();
        int bestCount = 0;

        void Consider(IReadOnlyList<IWebElement> items)
        {
            if (items.Count > bestCount)
            {
                best = items;
                bestCount = items.Count;
            }
        }

        IReadOnlyList<IWebElement> safeFind(Func<IReadOnlyList<IWebElement>> finder)
        {
            try
            {
                return finder();
            }
            catch
            {
                return Array.Empty<IWebElement>();
            }
        }

        var directItems = safeFind(() => FilterVisibleItems(notesList.FindElements(By.XPath("./div|./li"))));
        Consider(directItems);

        if (directItems.Count == 1)
        {
            var unwrapped = UnwrapSingleContainer(directItems[0]);
            Consider(unwrapped);
        }

        var roleItems = safeFind(() => FilterVisibleItems(notesList.FindElements(By.CssSelector("[role='listitem'],[role='option']"))));
        Consider(DeduplicateListItems(roleItems));

        var attrItems = safeFind(() => FilterVisibleItems(notesList.FindElements(By.CssSelector("[data-note-id],[data-id],[data-uuid],[data-key],[data-index],[data-row],[id]"))));
        Consider(DeduplicateListItems(attrItems));

        var classItems = safeFind(() => FilterVisibleItems(notesList.FindElements(By.CssSelector("[class*='note-item'],[class*='note-list-item'],[class*='note-card'],[class*='list-item']"))));
        Consider(DeduplicateListItems(classItems));

        if (allowGlobalFallback)
        {
            var globalCandidates = safeFind(() => FilterVisibleItems(_driver.FindElements(By.CssSelector(
                "[data-note-id],[data-id],[data-uuid],[data-key],[data-index],[data-row]," +
                "[class*='note-item'],[class*='note-list-item'],[class*='note-card'],[class*='list-item']," +
                "[role='listitem'],[role='option']"))));
            var dedupedGlobal = DeduplicateListItems(globalCandidates);
            if (dedupedGlobal.Count > bestCount)
            {
                var scrollContainer = TryFindScrollableContainer(dedupedGlobal[0]);
                if (scrollContainer is not null)
                {
                    resolvedContainer = scrollContainer;
                }

                best = dedupedGlobal;
                bestCount = dedupedGlobal.Count;
            }
        }

        return best;
    }

    private static IReadOnlyList<IWebElement> UnwrapSingleContainer(IWebElement container, int maxDepth = 3)
    {
        var current = container;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            IReadOnlyList<IWebElement> children;
            try
            {
                children = FilterVisibleItems(current.FindElements(By.XPath("./div|./li")));
            }
            catch
            {
                return Array.Empty<IWebElement>();
            }

            if (children.Count > 1)
            {
                return children;
            }

            if (children.Count == 1)
            {
                current = children[0];
                continue;
            }

            break;
        }

        return Array.Empty<IWebElement>();
    }

    private static IReadOnlyList<IWebElement> FilterVisibleItems(IReadOnlyCollection<IWebElement> items)
    {
        var result = new List<IWebElement>(items.Count);
        foreach (var item in items)
        {
            try
            {
                if (!item.Displayed)
                {
                    continue;
                }

                if (item.Size.Height <= 0 || item.Size.Width <= 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private IWebElement? TryFindScrollableContainer(IWebElement element)
    {
        try
        {
            var js = (IJavaScriptExecutor)_driver;
            var result = js.ExecuteScript(@"
                let el = arguments[0];
                while (el && el !== document.body) {
                    const style = window.getComputedStyle(el);
                    const overflowY = style ? style.overflowY : '';
                    if (el.scrollHeight > el.clientHeight && overflowY !== 'visible') {
                        return el;
                    }
                    el = el.parentElement;
                }
                return null;
            ", element);

            return result as IWebElement;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<IWebElement> DeduplicateListItems(IEnumerable<IWebElement> items)
    {
        var result = new List<IWebElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var key = TryGetListItemAttributeKey(item) ?? TryGetListItemTextKey(item);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = TryGetListItemKey(item);
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (seen.Add(key))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string? TryGetListItemAttributeKey(IWebElement listItem)
    {
        try
        {
            var candidates = new[] { "data-note-id", "data-id", "data-uuid", "data-key", "data-index", "data-row", "id" };
            foreach (var attr in candidates)
            {
                var value = listItem.GetAttribute(attr);
                if (attr == "id" && !string.IsNullOrWhiteSpace(value) && value.Trim().Length < 6)
                {
                    value = null;
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return "mi:" + value.Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetListItemKey(IWebElement listItem)
    {
        try
        {
            var candidates = new[]
            {
                "data-id",
                "data-note-id",
                "data-uuid",
                "data-key",
                "data-index",
                "data-row",
                "id"
            };

            foreach (var attr in candidates)
            {
                var value = listItem.GetAttribute(attr);
                if (attr == "id" && !string.IsNullOrWhiteSpace(value) && value.Trim().Length < 6)
                {
                    value = null;
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return "mi:" + value.Trim();
                }

                try
                {
                    var nested = listItem.FindElements(By.CssSelector($"[{attr}]"))?.FirstOrDefault();
                    if (nested is null)
                    {
                        continue;
                    }

                    var nestedValue = nested.GetAttribute(attr);
                    if (attr == "id" && !string.IsNullOrWhiteSpace(nestedValue) && nestedValue.Trim().Length < 6)
                    {
                        nestedValue = null;
                    }

                    if (!string.IsNullOrWhiteSpace(nestedValue))
                    {
                        return "mi:" + nestedValue.Trim();
                    }
                }
                catch
                {
                }
            }

            var outer = listItem.GetAttribute("outerHTML") ?? listItem.GetAttribute("innerHTML") ?? listItem.Text ?? string.Empty;
            outer = outer.Trim();
            if (!string.IsNullOrWhiteSpace(outer))
            {
                return "dom:" + Sha256Hex(outer);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetListItemTextKey(IWebElement listItem)
    {
        string text;
        try
        {
            text = listItem.Text ?? string.Empty;
        }
        catch
        {
            return null;
        }

        text = NormalizeKeyText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return "txt:" + Sha256Hex(text);
    }

    private static string? PickPreferredListKey(string? primary, string? textKey)
    {
        if (!string.IsNullOrWhiteSpace(primary) && primary.StartsWith("mi:", StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        if (!string.IsNullOrWhiteSpace(textKey))
        {
            return textKey;
        }

        return string.IsNullOrWhiteSpace(primary) ? null : primary;
    }

    private static string NormalizeKeyText(string s)
    {
        var t = (s ?? string.Empty).Replace("\r", "\n");
        t = Regex.Replace(t, "\\s+", " ").Trim();
        if (t.Length > 400)
        {
            t = t.Substring(0, 400);
        }
        return t;
    }

    private static string GetListWindowSignature(IReadOnlyList<IWebElement> items)
    {
        if (items.Count == 0)
        {
            return "0";
        }

        var head = TryGetListItemHint(items, fromEnd: false);
        var tail = TryGetListItemHint(items, fromEnd: true);
        return $"{items.Count}|{head}|{tail}";
    }

    private static string TryGetListItemHint(IReadOnlyList<IWebElement> items, bool fromEnd)
    {
        int max = Math.Min(items.Count, 6);
        for (int i = 0; i < max; i++)
        {
            int idx = fromEnd ? (items.Count - 1 - i) : i;
            var el = items[idx];

            var key = TryGetListItemKey(el);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return $"k:{key}";
            }

            string text;
            try
            {
                text = el.Text;
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (t.Length > 80)
                {
                    t = t.Substring(0, 80);
                }
                return $"t:{t}";
            }
        }

        return "";
    }

    private string? TryGetOpenedNoteKey(IWebElement noteContainer)
    {
        try
        {
            var js = (IJavaScriptExecutor)_driver;
            var attrKey = (string?)js.ExecuteScript(@"
const root = arguments[0];
const attrs = ['data-note-id','data-id','data-uuid','data-key'];
for (const a of attrs) {
  const v = root?.getAttribute?.(a);
  if (v && String(v).trim()) return String(v).trim();
}
let el = root;
for (let i = 0; i < 6 && el; i++) {
  for (const a of attrs) {
    const v = el?.getAttribute?.(a);
    if (v && String(v).trim()) return String(v).trim();
  }
  el = el.parentElement;
}
return '';
", noteContainer);

            if (!string.IsNullOrWhiteSpace(attrKey))
            {
                return "mi:" + attrKey.Trim();
            }
        }
        catch
        {
        }

        try
        {
            var href = _driver.Url ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(href))
            {
                var m = Regex.Matches(href, @"[A-Za-z0-9]{8,}")
                    .Select(x => x.Value)
                    .OrderByDescending(x => x.Length)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(m))
                {
                    return "url:" + m;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private string GetSplitNoteRelativePath(string exportName, string timeStampFormat, DateTime createdDate, string noteKey)
    {
        var baseDir = _baseDir;
        var baseName = $"note_{createdDate.ToString(timeStampFormat)}";
        var baseRel = Path.Combine(exportName, $"{baseName}.md");

        if (!File.Exists(Path.Combine(baseDir, baseRel)))
        {
            return baseRel;
        }

        var suffix = ExportManifest.KeySuffix(noteKey);
        var rel = Path.Combine(exportName, $"{baseName}_{suffix}.md");

        if (!File.Exists(Path.Combine(baseDir, rel)))
        {
            return rel;
        }

        for (int i = 2; i < 1000; i++)
        {
            var rel2 = Path.Combine(exportName, $"{baseName}_{suffix}_{i}.md");
            if (!File.Exists(Path.Combine(baseDir, rel2)))
            {
                return rel2;
            }
        }

        return rel;
    }

    private sealed class ExportManifest
    {
        public int Version { get; set; } = 1;

        public int? CloudTotalNotes { get; set; }

        public Dictionary<string, ManifestEntry> Entries { get; set; } = new();

        public sealed class ManifestEntry
        {
            public string Kind { get; set; } = "exported";
            public DateTime CreatedDate { get; set; }
            public string? Title { get; set; }
        }

        public bool Contains(string key) => Entries.ContainsKey(key);

        public void Upsert(string key, string kind, DateTime createdDate, string? title)
        {
            Entries[key] = new ManifestEntry
            {
                Kind = kind,
                CreatedDate = createdDate,
                Title = title
            };
        }

        public int CountByKind(string kind) => Entries.Values.Count(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));

        public void Save(string manifestPath)
        {
            var lines = new List<string>(Entries.Count + 1)
            {
                $"v{Version}"
            };

            if (CloudTotalNotes is not null && CloudTotalNotes.Value > 0)
            {
                lines.Add($"total|{CloudTotalNotes.Value}");
            }

            foreach (var kv in Entries.OrderBy(kv => kv.Value.CreatedDate))
            {
                var key = kv.Key;
                var entry = kv.Value;
                var keyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
                var titleBase64 = string.IsNullOrEmpty(entry.Title)
                    ? string.Empty
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Title));
                lines.Add($"{entry.Kind}|{entry.CreatedDate:O}|{keyBase64}|{titleBase64}");
            }

            File.WriteAllLines(manifestPath, lines, Encoding.UTF8);
        }

        public static ExportManifest Load(string manifestPath, string exportOutputPath, bool split)
        {
            var manifest = new ExportManifest();

            try
            {
                if (File.Exists(manifestPath))
                {
                    var lines = File.ReadAllLines(manifestPath, Encoding.UTF8);

                    if (lines.Length > 0 && lines[0].StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    {
                        var vText = lines[0].Substring(1);
                        if (int.TryParse(vText, out var v))
                        {
                            manifest.Version = v;
                        }
                    }

                    for (int i = 1; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split('|');
                        if (parts.Length == 2 && parts[0].Trim().Equals("total", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(parts[1].Trim(), out var total) && total > 0)
                            {
                                manifest.CloudTotalNotes = total;
                            }
                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (split)
                {
                    if (Directory.Exists(exportOutputPath))
                    {
                        foreach (var file in Directory.EnumerateFiles(exportOutputPath, "note_*", SearchOption.TopDirectoryOnly))
                        {
                            if (TryParseSingleNoteFile(file, out var kind, out var createdDate, out var title, out var content, out var key))
                            {
                                var finalKey = !string.IsNullOrWhiteSpace(key)
                                    ? key
                                    : BuildHashKey(createdDate, title ?? string.Empty, content ?? string.Empty);
                                manifest.Upsert(finalKey, kind, createdDate, title);
                            }
                        }
                    }
                }
                else
                {
                    if (File.Exists(exportOutputPath))
                    {
                        foreach (var block in ParseAggregatedMarkdown(File.ReadAllText(exportOutputPath, Encoding.UTF8)))
                        {
                            var finalKey = !string.IsNullOrWhiteSpace(block.Key)
                                ? block.Key
                                : BuildHashKey(block.CreatedDate, block.Title ?? string.Empty, block.Content ?? string.Empty);
                            manifest.Upsert(finalKey, block.Kind, block.CreatedDate, block.Title);
                        }
                    }
                }

                if (manifest.Entries.Count > 0)
                {
                    manifest.Save(manifestPath);
                }
            }
            catch
            {
            }

            return manifest;
        }

        public static string BuildHashKey(DateTime createdDate, string title, string content)
        {
            var payload = $"{createdDate:O}\n{NormalizeNewlines(title)}\n{NormalizeNewlines(content)}";
            return "hash:" + Sha256Hex(payload);
        }

        public static string KeySuffix(string key)
        {
            if (key.StartsWith("mi:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = key.Substring(3);
                var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
                if (cleaned.Length <= 10)
                {
                    return cleaned;
                }
                return cleaned.Substring(cleaned.Length - 10);
            }

            if (key.StartsWith("hash:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = key.Substring(5);
                return raw.Length <= 10 ? raw : raw.Substring(0, 10);
            }

            if (key.StartsWith("dom:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = key.Substring(4);
                return raw.Length <= 10 ? raw : raw.Substring(0, 10);
            }

            var cleanedFallback = new string(key.Where(char.IsLetterOrDigit).ToArray());
            return cleanedFallback.Length <= 10 ? cleanedFallback : cleanedFallback.Substring(0, 10);
        }

        private static string NormalizeNewlines(string s) => (s ?? string.Empty).Replace("\r\n", "\n");

        private static string Sha256Hex(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = SHA256.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static bool TryParseSingleNoteFile(string filePath, out string kind, out DateTime createdDate, out string? title, out string? content, out string? key)
        {
            kind = "exported";
            createdDate = default;
            title = null;
            content = null;
            key = null;

            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var block = ParseMarkdownBlock(lines);
            if (block is null)
            {
                return false;
            }

            kind = block.Value.Kind;
            createdDate = block.Value.CreatedDate;
            title = block.Value.Title;
            content = block.Value.Content;
            key = block.Value.Key;
            return true;
        }

        private static IEnumerable<(string Kind, DateTime CreatedDate, string? Title, string? Content, string? Key)> ParseAggregatedMarkdown(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var current = new List<string>();

            foreach (var line in lines)
            {
                if (line.Trim() == "****")
                {
                    if (current.Count > 0)
                    {
                        var parsed = ParseMarkdownBlock(current);
                        if (parsed is not null)
                        {
                            yield return parsed.Value;
                        }
                        current.Clear();
                    }

                    current.Add("****");
                    continue;
                }

                if (current.Count > 0)
                {
                    current.Add(line);
                }
            }

            if (current.Count > 0)
            {
                var parsed = ParseMarkdownBlock(current);
                if (parsed is not null)
                {
                    yield return parsed.Value;
                }
            }
        }

        private static (string Kind, DateTime CreatedDate, string? Title, string? Content, string? Key)? ParseMarkdownBlock(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0 || lines[0].Trim() != "****")
            {
                return null;
            }

            int createdAtIndex = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].StartsWith("*Created at:", StringComparison.OrdinalIgnoreCase))
                {
                    createdAtIndex = i;
                    break;
                }
            }

            if (createdAtIndex < 0)
            {
                return null;
            }

            var createdLine = lines[createdAtIndex];
            var createdText = createdLine.Trim().Trim('*');
            var createdValue = createdText.Substring("Created at:".Length).Trim();

            if (!DateTime.TryParseExact(createdValue, "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdDate))
            {
                return null;
            }

            int idx = 1;
            string? title = null;
            if (idx < createdAtIndex && lines[idx].StartsWith("## "))
            {
                title = lines[idx].Substring(3);
                idx++;
            }

            string? key = null;
            for (int i = createdAtIndex - 1; i >= idx; i--)
            {
                if (lines[i].StartsWith("*Key:", StringComparison.OrdinalIgnoreCase))
                {
                    var keyText = lines[i].Trim().Trim('*');
                    var keyValue = keyText.Substring("Key:".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(keyValue))
                    {
                        key = keyValue;
                    }
                    break;
                }
            }

            var contentLines = new List<string>();
            for (int i = idx; i < createdAtIndex; i++)
            {
                if (lines[i].StartsWith("*Key:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                contentLines.Add(lines[i]);
            }

            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
            {
                contentLines.RemoveAt(contentLines.Count - 1);
            }

            var restoredContentLines = contentLines.Select(l => l.EndsWith("  ") ? l.Substring(0, l.Length - 2) : l);
            var content = string.Join("\n", restoredContentLines);

            var kind = content.TrimStart().StartsWith("**Unsupported note type", StringComparison.OrdinalIgnoreCase)
                ? "unsupported"
                : "exported";

            return (kind, createdDate, title, content, key);
        }
    }

    private bool IsAtListBottom(IWebElement notesList)
    {
        try
        {
            var js = (IJavaScriptExecutor)_driver;
            return Convert.ToBoolean(js.ExecuteScript("return arguments[0].scrollTop + arguments[0].clientHeight >= arguments[0].scrollHeight - 5;", notesList));
        }
        catch
        {
            return false;
        }
    }

    private bool IsSpinnerVisible()
    {
        try
        {
            var js = (IJavaScriptExecutor)_driver;
            var styleObj = js.ExecuteScript(@"
                const el = document.querySelector('body div.spinner');
                if (!el) return null;
                return el.getAttribute('style') || '';
            ");

            if (styleObj is not string style)
            {
                return false;
            }

            if (style.Contains("display: none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var visibleObj = js.ExecuteScript(@"
                const el = document.querySelector('body div.spinner');
                if (!el) return false;
                const cs = window.getComputedStyle(el);
                if (!cs) return false;
                return cs.display !== 'none' && cs.visibility !== 'hidden' && cs.opacity !== '0';
            ");

            return visibleObj is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private void WaitForSpinnerIdle(TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        var spinnerEverVisible = IsSpinnerVisible();

        if (!spinnerEverVisible)
        {
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(2))
            {
                if (IsSpinnerVisible())
                {
                    spinnerEverVisible = true;
                    break;
                }
                Thread.Sleep(100);
            }
        }

        if (!spinnerEverVisible)
        {
            Thread.Sleep(800);
            return;
        }

        try
        {
            _driver.GetWait(timeout).Until(_ => !IsSpinnerVisible());
        }
        catch
        {
        }
    }

    private void SaveToFile(string fileName, string content, DateTime createdDate, string? title = null, string? noteKey = null)
    {
        var targetPath = Path.Combine(_baseDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? _baseDir);
        using StreamWriter sw = File.AppendText(targetPath);

        sw.WriteLine("****");

        if (!string.IsNullOrEmpty(title))
        {
            sw.WriteLine($"## {title}");
        }

        // add newline preserve to format the note properly
        sw.WriteLine(content.EscapeNewLine());
        sw.WriteLine();

        if (!string.IsNullOrWhiteSpace(noteKey))
        {
            sw.WriteLine($"*Key: {noteKey.Trim()}*");
        }

        sw.WriteLine($"*Created at: {createdDate.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}*");
    }

    private static void SaveImage(string path, string? src, IEnumerable<OpenQA.Selenium.Cookie> cookies)
    {
        if (File.Exists(path))
        {
            return;
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer()
        };

        foreach (var cookie in cookies)
        {
            handler.CookieContainer.Add(
                new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                );
        }

        using var client = new HttpClient(handler);

        try
        {
            byte[] imageBytes = client.GetByteArrayAsync(src).Result;
            File.WriteAllBytes(path, imageBytes);
        }
        catch (Exception e)
        {
            Console.WriteLine($"\n{"[ERROR]".Pastel(Color.Red)} Couldn't fetch image.\nError: {e.Message}");
        }
    }

    private static void GetCreatedDate(string createdString, out DateTime createdDate)
    {
        if (createdString.ToLower().Contains("now", StringComparison.InvariantCultureIgnoreCase))
        {
            createdDate = DateTime.Now; // get current date
        }
        else if (createdString.ToLower().Contains("yesterday", StringComparison.InvariantCultureIgnoreCase))
        {
            createdDate = DateTime.Now.AddDays(-1).Date; // get yesterday's date
        }
        else if (createdString.EndsWith("ago"))
        {
            createdDate = RelativeTimeParser.Parse(createdString);
        }
        else if (SimplifiedDateParser.TryParseMdHm(createdString, out DateTime parsedSimple))
        {
            createdDate = parsedSimple;
        }
        else
        {
            var formats = new[]
            {
                "dd/MM/yyyy HH:mm",
                "MM/dd/yyyy HH:mm",
                "yyyy/MM/dd HH:mm",
                "dd/MM/yyyy H:mm",
                "MM/dd/yyyy H:mm",
                "M/d/yyyy h:mm tt",
                "MM/dd/yyyy hh:mm tt"
            };

            if (!DateTime.TryParseExact(createdString, formats, new CultureInfo("en-US"), DateTimeStyles.None, out createdDate))
            {
                createdDate = DateTime.Now; // fallback to current date
            }
        }
    }

    private void ExecuteScroll(IWebElement notesList, IWebElement currentElement)
    {
        try
        {
            var js = (IJavaScriptExecutor)_driver;
            js.ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", currentElement);
            js.ExecuteScript("arguments[0].scrollBy(0, 800);", notesList);
        }
        catch {}
    }

    [GeneratedRegex("[^\\d]")]
    private static partial Regex DigitRegex();
}

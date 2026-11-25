using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Input.Inking;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace SurfaceNotes
{
    /// <summary>Dialogs, the message bar, handwriting to text, and About and updates.</summary>
    public sealed partial class MainPage
    {
        // Windows allows one dialog at a time; a second one would crash the app, so they take turns.
        private readonly SemaphoreSlim dialogLock = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        private string noticeTag;
        private Func<Task> noticeAction;

        private void InitDialogs()
        {
            noticeTimer.Tick += (s, e) => HideNotice();
        }

        // ---------------------------------------------------------------- message bar

        /// <param name="tag">What the message is about, so it can be cleared when that's resolved.</param>
        private void ShowNotice(string text, string tag, bool autoHide = false, string actionText = null, Func<Task> action = null)
        {
            NoticeText.Text = text;
            noticeTag = tag;
            noticeAction = action;
            NoticeAction.Content = actionText;
            NoticeAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            Notice.Visibility = Visibility.Visible;
            noticeTimer.Stop();
            if (autoHide) noticeTimer.Start();
        }

        private void HideNotice(string tag = null)
        {
            if (tag != null && tag != noticeTag) return;
            Notice.Visibility = Visibility.Collapsed;
            noticeTimer.Stop();
            noticeTag = null;
            noticeAction = null;
        }

        private async void NoticeAction_Click(object sender, RoutedEventArgs e)
        {
            var action = noticeAction;
            HideNotice();
            if (action != null) await RunAsync(action);
        }

        private void NoticeClose_Click(object sender, RoutedEventArgs e)
        {
            HideNotice();
        }

        // ---------------------------------------------------------------- dialogs

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            await dialogLock.WaitAsync();
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                dialogLock.Release();
            }
        }

        private async Task<string> AskTextAsync(string title, string initial, string primary)
        {
            var box = new TextBox { Text = initial };
            box.Loaded += (s, e) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = primary,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await ShowDialogAsync(dialog);
            return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
        }

        private async Task<bool> ConfirmAsync(string title, string message, string primary, string close = "Cancel", bool defaultToPrimary = false)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = close,
                DefaultButton = defaultToPrimary ? ContentDialogButton.Primary : ContentDialogButton.Close,
            };
            return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
            };
            await ShowDialogAsync(dialog);
        }

        private sealed class NewNotebookAnswer
        {
            public string Name;
            public bool ChangeLocation;
        }

        /// <summary>Asks for a name and shows where the notebook will be saved. Null if cancelled.</summary>
        private async Task<NewNotebookAnswer> AskNewNotebookAsync(string name, StorageFolder location)
        {
            var box = new TextBox { Text = name, PlaceholderText = "Notebook name" };
            box.Loaded += (s, e) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock { Text = "Saved in", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
            panel.Children.Add(new TextBlock
            {
                Text = location != null ? Notebook.DescribeFolder(location.Path) : "Choose a folder, for example one in OneDrive.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0x5B, 0x65, 0x75)),
            });
            var change = new HyperlinkButton { Content = location != null ? "Change\u2026" : "Choose a folder\u2026", Padding = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(change);

            var dialog = new ContentDialog
            {
                Title = "New notebook",
                Content = panel,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            bool changeLocation = false;
            change.Click += (s, e) => { changeLocation = true; dialog.Hide(); };

            var result = await ShowDialogAsync(dialog);
            if (!changeLocation && result != ContentDialogResult.Primary) return null;
            return new NewNotebookAnswer { Name = box.Text.Trim(), ChangeLocation = changeLocation };
        }

        // ---------------------------------------------------------------- handwriting to text

        private sealed class RecognizedWord
        {
            public Rect Rect;
            public string Text;
        }

        private async void ToText_Click(object sender, RoutedEventArgs e)
        {
            if (busy || !pageLoaded) return;
            if (Strokes.GetStrokes().Count == 0)
            {
                await ShowMessageAsync("Nothing to convert", "Write something on the page first.");
                return;
            }
            var target = Strokes.GetStrokes().Any(s => s.Selected) ? InkRecognitionTarget.Selected : InkRecognitionTarget.All;

            string text;
            try
            {
                var recognizer = new InkRecognizerContainer(); // runs on the device, no internet needed
                var results = await recognizer.RecognizeAsync(Strokes, target);
                // Put each word on the line it was written on, left to right.
                var words = results
                    .Select(r => new RecognizedWord { Rect = r.BoundingRect, Text = r.GetTextCandidates().FirstOrDefault() ?? "" })
                    .OrderBy(w => w.Rect.Top)
                    .ToList();
                var lines = new List<List<RecognizedWord>>();
                foreach (var w in words)
                {
                    var line = lines.LastOrDefault();
                    double mid = w.Rect.Top + w.Rect.Height / 2;
                    if (line != null && mid < line.Max(x => x.Rect.Bottom)) line.Add(w);
                    else lines.Add(new List<RecognizedWord> { w });
                }
                text = string.Join("\r\n", lines.Select(l => string.Join(" ", l.OrderBy(x => x.Rect.Left).Select(x => x.Text))));
            }
            catch (Exception ex)
            {
                Log.Error("Handwriting recognition", ex);
                await ShowMessageAsync("Handwriting recognition isn't available",
                    "Install it in Windows Settings > Time & language > Language & region > your language > Language options > Handwriting.");
                return;
            }

            var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 400 };
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            var dialog = new ContentDialog
            {
                Title = target == InkRecognitionTarget.Selected ? "Selection as text" : "Page as text",
                Content = box,
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                var data = new DataPackage();
                data.SetText(box.Text);
                Clipboard.SetContent(data);
            }
        }

        // ---------------------------------------------------------------- about & updates

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            Split.IsPaneOpen = false;
            var v = Package.Current.Id.Version;
            var gray = new SolidColorBrush(Colors.Gray);
            var status = new TextBlock { Text = "Checking for updates\u2026", TextWrapping = TextWrapping.Wrap, Foreground = gray };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = string.Format("Version {0}.{1}.{2}", v.Major, v.Minor, v.Build), FontSize = 18 });
            panel.Children.Add(status);
            panel.Children.Add(new TextBlock
            {
                Text = "Each notebook is a folder, and each page in it is a picture (.gif) with your ink saved inside.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = gray,
                Margin = new Thickness(0, 8, 0, 0),
            });
            var dialog = new ContentDialog
            {
                Title = "Surface Notes",
                Content = panel,
                PrimaryButtonText = "Update now",
                IsPrimaryButtonEnabled = false,
                CloseButtonText = "Close",
            };
            dialog.PrimaryButtonClick += async (d, args) =>
            {
                args.Cancel = true; // keep the dialog open to show progress
                d.IsPrimaryButtonEnabled = false;
                status.Text = "Saving your notes and installing the update. Surface Notes closes when it's done; open it again from the Start menu.";
                await SaveNowAsync();
                if (dirty)
                {
                    status.Text = "Your latest ink couldn't be saved, so the update didn't start. Close this and try again in a moment.";
                    d.IsPrimaryButtonEnabled = true;
                    return;
                }
                string error = await InstallUpdateAsync();
                if (error != null)
                {
                    status.Text = error;
                    d.IsPrimaryButtonEnabled = true;
                }
                else
                {
                    status.Text = "The update is installed. Close Surface Notes and open it again to use it.";
                }
            };

            await dialogLock.WaitAsync();
            try
            {
                var showing = dialog.ShowAsync();
                await CheckForUpdateAsync(status, dialog);
                await showing;
            }
            catch (Exception ex)
            {
                Log.Error("About", ex);
            }
            finally
            {
                dialogLock.Release();
            }
        }

        private const string AppInstallerUrl = "https://malicious-sol.github.io/surface-notes/SurfaceNotes.appinstaller";

        // The query string skips GitHub's ~10 minute cache so a new version shows up right away.
        private static Uri FreshAppInstallerUri()
        {
            return new Uri(AppInstallerUrl + "?t=" + DateTime.UtcNow.Ticks);
        }

        private static async Task CheckForUpdateAsync(TextBlock status, ContentDialog dialog)
        {
            if (Package.Current.IsDevelopmentMode)
            {
                status.Text = "This copy was installed from Visual Studio, so it can't update itself. Run the install command once to switch to the self-updating version.";
                return;
            }
            try
            {
                Version latest;
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                    var xml = await http.GetStringAsync(FreshAppInstallerUri());
                    latest = Version.Parse(System.Xml.Linq.XDocument.Parse(xml).Root.Attribute("Version").Value);
                }
                var v = Package.Current.Id.Version;
                var current = new Version(v.Major, v.Minor, v.Build, v.Revision);
                if (latest > current)
                {
                    status.Text = string.Format("Version {0}.{1}.{2} is available.", latest.Major, latest.Minor, latest.Build);
                    dialog.IsPrimaryButtonEnabled = true;
                }
                else
                {
                    status.Text = "You have the latest version.";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Update check", ex);
                status.Text = "Couldn't check for updates right now. Make sure you're online and try again.";
            }
        }

        /// <summary>Installs the newest version from the download page. Returns an error message, or null on success.</summary>
        private static async Task<string> InstallUpdateAsync()
        {
            try
            {
                var pm = new PackageManager();
                // The app is closed while the new version installs.
                var result = await pm.AddPackageByAppInstallerFileAsync(
                    FreshAppInstallerUri(), AddPackageByAppInstallerOptions.ForceTargetAppShutdown, pm.GetDefaultPackageVolume());
                if (result.ExtendedErrorCode != null) return "The update didn't install: " + result.ErrorText;
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Installing update", ex);
                return "The update didn't install: " + ex.Message;
            }
        }
    }
}

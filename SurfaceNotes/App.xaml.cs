using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SurfaceNotes
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            EnteredBackground += OnEnteredBackground;
            UnhandledException += OnUnhandledException;
        }

        private static MainPage CurrentPage
        {
            get { return (Window.Current?.Content as Frame)?.Content as MainPage; }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }
            Window.Current.Activate();
        }

        // Minimized, switched away from, or closing: save now.
        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var page = CurrentPage;
                if (page != null) await page.SaveNowAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Saving when going to the background", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                var page = CurrentPage;
                if (page != null) await page.SaveOrRescueAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Saving on suspend", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        // A bug somewhere shouldn't close the window and take unsaved ink with it: log it, save, and keep going.
        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Error("Unexpected error", e.Exception);
            e.Handled = true;
            try
            {
                var page = CurrentPage;
                if (page != null)
                {
                    var ignored = page.SaveNowAsync();
                }
            }
            catch
            {
            }
        }
    }
}

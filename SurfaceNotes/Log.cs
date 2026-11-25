using System;
using System.Threading;
using Windows.Storage;

namespace SurfaceNotes
{
    /// <summary>
    /// A small error log for troubleshooting, kept in the app's own folder:
    /// %LOCALAPPDATA%\Packages\SurfaceNotes.Local_*\LocalState\log.txt
    /// Only problems are written, so it costs nothing while things work.
    /// </summary>
    internal static class Log
    {
        private const ulong MaxSize = 256 * 1024;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static void Error(string what, Exception ex)
        {
            Write(what + ": " + ex);
        }

        public static async void Write(string message)
        {
            try
            {
                await Gate.WaitAsync();
                try
                {
                    var folder = ApplicationData.Current.LocalFolder;
                    var file = await folder.CreateFileAsync("log.txt", CreationCollisionOption.OpenIfExists);
                    var props = await file.GetBasicPropertiesAsync();
                    if (props.Size > MaxSize)
                    {
                        file = await folder.CreateFileAsync("log.txt", CreationCollisionOption.ReplaceExisting);
                    }
                    await FileIO.AppendTextAsync(file, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch
            {
                // Logging must never cause a problem of its own.
            }
        }
    }
}

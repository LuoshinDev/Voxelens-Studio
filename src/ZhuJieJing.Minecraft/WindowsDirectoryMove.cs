namespace ZhuJieJing.Minecraft;

internal static class WindowsDirectoryMove
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private static readonly int[] RetryDelaysMilliseconds = [20, 40, 80, 160, 320];

    /// <summary>
    /// Publishes a prepared directory while tolerating short-lived Windows scanner/indexer handles.
    /// A semantic conflict (the source vanished or the destination appeared) is never retried.
    /// </summary>
    public static void Move(string sourceDirectory, string destinationDirectory)
    {
        for(int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(sourceDirectory, destinationDirectory);
                return;
            }
            catch(Exception exception) when(
                IsPotentiallyTransient(exception) &&
                attempt < RetryDelaysMilliseconds.Length &&
                Directory.Exists(sourceDirectory) &&
                !Directory.Exists(destinationDirectory) &&
                !File.Exists(destinationDirectory))
            {
                Thread.Sleep(RetryDelaysMilliseconds[attempt]);
            }
        }
    }

    private static bool IsPotentiallyTransient(Exception exception) =>
        exception.HResult is AccessDeniedHResult or SharingViolationHResult or LockViolationHResult;
}

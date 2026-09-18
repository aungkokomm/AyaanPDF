using System.IO.Pipes;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>The PDFs Windows asked the app to open, from its command line.</summary>
public static class LaunchFiles
{
    /// <summary>
    /// Each argument that names an existing PDF, as a full path, once each.
    /// Explorer passes full paths; a path typed in a terminal is relative to
    /// the folder it was typed in, which only this process knows, so it is
    /// resolved here before being passed to another one.
    /// </summary>
    public static List<string> From(IEnumerable<string> args, string currentDirectory)
    {
        var files = new List<string>();
        foreach (string arg in args)
        {
            string path = arg.Trim().Trim('"');
            if (path.Length == 0 || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string full;
            try
            {
                full = Path.GetFullPath(path, currentDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (File.Exists(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(full);
            }
        }
        return files;
    }
}

/// <summary>
/// Keeps Ayaan PDF to one window per user: a second launch hands its files to
/// the first one, which opens them as tabs, and then quits.
/// </summary>
/// <remarks>
/// The first launch creates a named mutex and listens on a named pipe. A later
/// launch finds the mutex, sends its files' full paths down the pipe, one per
/// line, and exits. Both are per user and per session, and the pipe refuses
/// other users. If the first launch does not answer in time (it is closing,
/// or stuck), the later one opens its own window rather than nothing.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly Action<IReadOnlyList<string>> _received;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipe, Action<IReadOnlyList<string>> received)
    {
        _mutex = mutex;
        _pipe = pipe;
        _received = received;
    }

    /// <summary>
    /// One name per user, session and install folder: two people signed in at
    /// once each get their own window, and a portable copy or a test build in
    /// another folder keeps its own rather than handing its files to the
    /// installed one.
    /// </summary>
    public static string NameFor(string app, string folder)
    {
        string where = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..12];
        return $"{app.Replace(' ', '-')}-{Environment.UserDomainName}-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}-{where}";
    }

    /// <summary>
    /// Becomes the one instance and starts listening, or null when another
    /// already is. <paramref name="received"/> is called off the UI thread
    /// with each later launch's files, possibly none.
    /// </summary>
    public static SingleInstance? TryClaim(string name, Action<IReadOnlyList<string>> received)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        var instance = new SingleInstance(mutex, name, received);
        _ = instance.ListenAsync();
        return instance;
    }

    /// <summary>Hands files to the instance already running. False if it did not take them in time.</summary>
    public static bool TrySend(string name, IReadOnlyList<string> files, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            client.Write(Encoding.UTF8.GetBytes(string.Join('\n', files)));
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string message = await reader.ReadToEndAsync(_stop.Token);
                _received(message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A launch that gave up half way. The next one gets a fresh pipe.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.Dispose();
    }
}

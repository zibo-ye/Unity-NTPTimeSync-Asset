using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// A high-frequency, pause-aware NTP time client using a Stopwatch.
/// This version mitigates the issues of application pausing by stopping the timer
/// during a pause and forcing a fresh synchronization upon resuming.
/// </summary>
public class NtpDateTime : MonoSingleton<NtpDateTime>
{
    [Header("NTP Settings")]
    [Tooltip("The NTP server to use for time synchronization.")]
    [SerializeField] private string _ntpServer = "time.google.com";

    [Tooltip("The maximum time in seconds to wait for a response from the server.")]
    [SerializeField][Range(1, 10)] private int _requestTimeout = 5;

    [Header("Synchronization Settings")]
    [Tooltip("How often, in seconds, to re-synchronize with the NTP server to correct drift.")]
    [SerializeField][Range(10, 3600)] private int _syncIntervalSeconds = 60;

    public bool IsTimeSynchronized { get; private set; }

    public DateTime Now
    {
        get
        {
            if (IsTimeSynchronized && _elapsedTimer != null && _elapsedTimer.IsRunning)
            {
                return _ntpDate.Add(_elapsedTimer.Elapsed);
            }
            // Fallback while not synchronized or if the timer isn't running (e.g., during a pause).
            return DateTime.Now;
        }
    }

    private DateTime _ntpDate;
    private Stopwatch _elapsedTimer;
    private bool _isSynchronizing;
    private CancellationTokenSource _cts;

    private void Start()
    {
        _cts = new CancellationTokenSource();
        StartContinuousSync(_cts.Token);
    }

    protected void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public async void ManualSynchronizeTime()
    {
        Debug.Log("Manual NTP re-synchronization.");
        await SynchronizeAsync();
    }

    /// <summary>
    /// Handles the application's pause and resume events. This is the key to making the Stopwatch approach reliable.
    /// </summary>
    /// <param name="isPaused">True if the application is pausing, false if it is resuming.</param>
    private void OnApplicationPause(bool isPaused)
    {
        if (!isPaused)
        {
            ManualSynchronizeTime();
        }
    }
    private void OnApplicationFocus(bool isFocused)
    {
        if (isFocused)
        {
            ManualSynchronizeTime();
        }
    }

    public async Task<bool> SynchronizeAsync()
    {
        if (_isSynchronizing) return false;

        _isSynchronizing = true;
        try
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                Debug.LogWarning("No Internet connection. Cannot synchronize NTP time.");
                return false;
            }

            var ntpTime = await GetNtpTimeAsync();

            // Calculate error before updating _ntpDate
            TimeSpan error = TimeSpan.Zero;
            if (IsTimeSynchronized && _elapsedTimer != null && _elapsedTimer.IsRunning)
            {
                var localNow = _ntpDate.Add(_elapsedTimer.Elapsed);
                error = ntpTime - localNow;
            }

            _ntpDate = ntpTime;
            IsTimeSynchronized = true;
            _elapsedTimer = Stopwatch.StartNew();

            Debug.Log($"<color=lime>Time synchronized:</color> {Now:HH:mm:ss.fff} | <color=yellow>Correction: {error.TotalMilliseconds:F2} ms</color>");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"NTP synchronization failed: {e.Message}");
            _elapsedTimer?.Stop();
            IsTimeSynchronized = false;
            return false;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private async void StartContinuousSync(CancellationToken token)
    {
        Debug.Log("Starting continuous NTP synchronization...");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_syncIntervalSeconds), token);
            while (!token.IsCancellationRequested)
            {
                await SynchronizeAsync();
                await Task.Delay(TimeSpan.FromSeconds(_syncIntervalSeconds), token);
            }
        }
        catch (TaskCanceledException)
        {
            Debug.Log("NTP synchronization loop canceled.");
        }
    }

    private async Task<DateTime> GetNtpTimeAsync()
    {
        var ntpData = new byte[48];
        ntpData[0] = 0x1B;

        using var udpClient = new UdpClient();
        var addresses = await Dns.GetHostAddressesAsync(_ntpServer);
        var ipEndPoint = new IPEndPoint(addresses[0], 123);

        using var timeoutCts = new CancellationTokenSource(_requestTimeout * 1000);
        var receiveTask = udpClient.SendAsync(ntpData, ntpData.Length, ipEndPoint)
                                   .ContinueWith(task => udpClient.ReceiveAsync(), timeoutCts.Token)
                                   .Unwrap();

        if (await Task.WhenAny(receiveTask, Task.Delay(-1, timeoutCts.Token)) == receiveTask)
        {
            var receivedResults = await receiveTask;
            var receivedNtpData = receivedResults.Buffer;

            ulong intPart = ((ulong)receivedNtpData[40] << 24) | ((ulong)receivedNtpData[41] << 16) | ((ulong)receivedNtpData[42] << 8) | receivedNtpData[43];
            ulong fractPart = ((ulong)receivedNtpData[44] << 24) | ((ulong)receivedNtpData[45] << 16) | ((ulong)receivedNtpData[46] << 8) | receivedNtpData[47];
            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);

            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)milliseconds).ToLocalTime();
        }
        else
        {
            throw new TimeoutException($"The NTP request to '{_ntpServer}' timed out.");
        }
    }
}
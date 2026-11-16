using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GameDub.Core;

namespace GameDub.Lights;

/// <summary>
/// Forza's "Data Out" telemetry (Forza Horizon 4/5, Forza Motorsport 7/2023): the game sends its car's state 60 times
/// a second as UDP to 127.0.0.1:5300 (set once in the game: Settings -> HUD and Gameplay -> Data Out). It gives the brake
/// pedal, the engine speed for a shift light, and crashes from the car's acceleration: nothing is guessed from sound.
/// The Steam build sends to the console itself as is; a Microsoft Store build would need a loopback exemption.
/// </summary>
public sealed class Telemetry : IDisposable
{
    public const int Port = 5300;
    // A crash: a horizontal jolt far over what tyres can do (about 1.5-2 g). Landings show on the vertical axis, left out.
    const double CrashAccel = 35; // m/s², about 3.6 g

    readonly UdpClient _udp;
    readonly Thread _thread;
    volatile bool _stop;
    long _lastTick;                // last packet with a race on
    volatile float _brake, _throttle, _rpm, _maxRpm;
    long _crashTick;
    // for the log: a minute of racing
    double _maxJolt; int _crashes; long _statTick;

    /// <summary>A crash, how hard 0..1: 0 just over the threshold, 1 from about 8.7 g.</summary>
    public event Action<double>? Crash;

    public Telemetry()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        _udp.Client.ReceiveTimeout = 1000;
        _thread = new Thread(Run) { IsBackground = true, Name = "forza telemetry", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Write($"telemetry: listening on 127.0.0.1:{Port} (Forza Data Out)");
    }

    /// <summary>A race is on and packets come (within the last second).</summary>
    public bool Live => Environment.TickCount64 - Interlocked.Read(ref _lastTick) < 1000;
    /// <summary>Brake pedal, 0..1.</summary>
    public double Brake => _brake;
    /// <summary>Throttle pedal, 0..1.</summary>
    public double Throttle => _throttle;
    /// <summary>Engine speed as a share of its maximum, 0..1.</summary>
    public double Rev => _maxRpm > 0 ? Math.Clamp(_rpm / _maxRpm, 0, 1) : 0;

    void Run()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            byte[] p;
            try { p = _udp.Receive(ref from); }
            catch (SocketException) { continue; }        // a timeout: look at _stop again
            catch (ObjectDisposedException) { return; }
            try { Parse(p); } catch (Exception ex) { Log.Error("telemetry", ex); }
        }
    }

    void Parse(byte[] p)
    {
        if (p.Length < 232 || BitConverter.ToInt32(p, 0) == 0) return; // IsRaceOn is 0 in menus and on pause
        long now = Environment.TickCount64;
        _maxRpm = BitConverter.ToSingle(p, 8);
        _rpm = BitConverter.ToSingle(p, 16);
        float ax = BitConverter.ToSingle(p, 20), az = BitConverter.ToSingle(p, 28);
        // The dash part follows the 232-byte sled: 12 bytes later in Forza Horizon (324-byte packets), right after it
        // in Motorsport (311 and 331 bytes). Speed at +12, throttle and brake bytes at +71 and +72.
        int dash = p.Length == 324 ? 244 : p.Length >= 311 ? 232 : -1;
        float speed = 0;
        if (dash > 0) { speed = BitConverter.ToSingle(p, dash + 12); _throttle = p[dash + 71] / 255f; _brake = p[dash + 72] / 255f; }
        Interlocked.Exchange(ref _lastTick, now);

        double jolt = Math.Sqrt(ax * ax + az * az);
        _maxJolt = Math.Max(_maxJolt, jolt);
        if (jolt > CrashAccel && (dash < 0 || speed > 3) && now - _crashTick > 700)
        {
            _crashTick = now; _crashes++;
            Log.Write($"telemetry: crash {jolt / 9.81:F1} g");
            try { Crash?.Invoke(Math.Clamp((jolt - CrashAccel) / 50, 0, 1)); } catch (Exception ex) { Log.Error("telemetry crash", ex); }
        }
        if (now - _statTick > 60_000)
        {
            if (_statTick > 0) Log.Write($"telemetry: a minute of racing, max jolt {_maxJolt / 9.81:F1} g, {_crashes} crashes");
            _statTick = now; _maxJolt = 0; _crashes = 0;
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _udp.Close(); } catch { }
    }
}

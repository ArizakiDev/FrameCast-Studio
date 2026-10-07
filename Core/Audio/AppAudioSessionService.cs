using NAudio.CoreAudioApi;

namespace FrameCastStudio.Core.Audio;

/// <summary>Enumerates active per-application audio sessions on the default render device.</summary>
public static class AppAudioSessionService
{
    /// <summary>Returns all active, non-system audio sessions on the default playback device.</summary>
    public static IReadOnlyList<AppAudioSession> GetSessions()
    {
        var result = new List<AppAudioSession>();
        try
        {
            using var e = new MMDeviceEnumerator();
            using var dev = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mgr = dev.AudioSessionManager;
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var ctrl = sessions[i];
                // skip system / silent sessions
                if (ctrl.GetProcessID == 0) continue;
                if ((int)ctrl.State == 2) continue; // 2 = AudioSessionStateExpired
                try { result.Add(new AppAudioSession(ctrl)); }
                catch { ctrl.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Write("AppAudioSessions: " + ex.Message); }
        return result;
    }
}

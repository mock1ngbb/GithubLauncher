using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using GithubLauncher.Services.Logging;

#if WINDOWS
using NAudio.Wave;
#endif

namespace GithubLauncher.Services
{
    public class MusicPlayerService : INotifyPropertyChanged
    {
        private const int FADE_DURATION_MS = 500;

        private string _musicPath = string.Empty;
        private float _musicVolume = 0.2f;
        private CancellationTokenSource? _fadeTaskCts;
        private bool _musicPausedByDeactivation = false;

        #if WINDOWS
        private IWavePlayer? _waveOut;
        private AudioFileReader? _audioFileReader;
        #endif

        private Process? _musicProcess;

        /// <summary>
        /// Raised when the music player state changes in a way the UI may need to observe.
        /// </summary>
        public event Action? MusicStopped;
        public event Action? MusicStarted;

        /// <summary>
        /// Path to the currently selected music file.
        /// </summary>
        public string MusicPath
        {
            get => _musicPath;
            set
            {
                if (_musicPath != value)
                {
                    _musicPath = value;
                    OnPropertyChanged(nameof(MusicPath));
                }
            }
        }

        /// <summary>
        /// Current playback volume (0.0 – 1.0).
        /// </summary>
        public float Volume
        {
            get => _musicVolume;
            set
            {
                if (Math.Abs(_musicVolume - value) > 0.001f)
                {
                    _musicVolume = value;
                    OnPropertyChanged(nameof(Volume));

                    #if WINDOWS
                    if (_audioFileReader != null)
                    {
                        _audioFileReader.Volume = value;
                    }
                    #else
                    if (!string.IsNullOrEmpty(MusicPath) && File.Exists(MusicPath))
                    {
                        PlayLauncherMusic(MusicPath);
                    }
                    #endif
                }
            }
        }

        /// <summary>
        /// Whether music was paused because the window was deactivated.
        /// </summary>
        public bool MusicPausedByDeactivation
        {
            get => _musicPausedByDeactivation;
            set => _musicPausedByDeactivation = value;
        }

        // ── Playback control ───────────────────────────────────────────────

        public void Play(string path, float volume)
        {
            Volume = volume;
            PlayLauncherMusic(path);
        }

        public void Stop()
        {
            StopLauncherMusic();
        }

        public void PlayLauncherMusic(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                StopLauncherMusic();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    PlayMusicWindows(path);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    PlayMusicLinux(path);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    PlayMusicMac(path);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to play launcher music", ex);
            }
        }

        private void PlayMusicWindows(string path)
        {
            #if WINDOWS
            try
            {
                _audioFileReader = new AudioFileReader(path);
                _audioFileReader.Volume = _musicVolume;

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_audioFileReader);

                // Enable looping
                _waveOut.PlaybackStopped += (sender, args) =>
                {
                    if (_audioFileReader != null && _waveOut != null)
                    {
                        _audioFileReader.Position = 0;
                        _waveOut.Play();
                    }
                };

                _waveOut.Play();
                MusicStarted?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("NAudio playback failed", ex);
            }
            #else
            Log.Debug("Windows audio playback not available on this platform");
            #endif
        }

        private void PlayMusicLinux(string path)
        {
            string[] players = { "ffplay", "mpv", "cvlc", "mplayer" };

            foreach (var player in players)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = player,
                        Arguments = player switch
                        {
                            "ffplay" => $"-nodisp -autoexit -loop 0 -volume {(int)(_musicVolume * 100)} \"{path}\"",
                            "mpv" => $"--no-video --loop=inf --volume={_musicVolume * 100} \"{path}\"",
                            "cvlc" => $"--no-video --loop --volume {(int)(_musicVolume * 512)} \"{path}\"",
                            "mplayer" => $"-loop 0 -volume {(int)(_musicVolume * 100)} \"{path}\"",
                            _ => $"\"{path}\""
                        },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _musicProcess = Process.Start(psi);
                    if (_musicProcess != null)
                    {
                        _musicProcess.EnableRaisingEvents = true;
                        Log.Info($"Playing music with {player}");
                        MusicStarted?.Invoke();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to start {player}", ex);
                    continue;
                }
            }

            Log.Warn("No suitable audio player found on Linux. Install one of: ffplay, mpv, vlc, mplayer");
        }

        private void PlayMusicMac(string path)
        {
            try
            {
                var volumeValue = _musicVolume * 255f;

                var psi = new ProcessStartInfo
                {
                    FileName = "afplay",
                    Arguments = $"-v {volumeValue} \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _musicProcess = Process.Start(psi);

                if (_musicProcess != null)
                {
                    _musicProcess.EnableRaisingEvents = true;
                    _musicProcess.Exited += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(MusicPath) && File.Exists(MusicPath))
                        {
                            try
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (!string.IsNullOrEmpty(MusicPath))
                                    {
                                        PlayMusicMac(MusicPath);
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Failed to restart music", ex);
                            }
                        }
                    };

                    Log.Info($"Playing music with afplay at volume {volumeValue}");
                    MusicStarted?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error("afplay failed", ex);
            }
        }

        public void StopLauncherMusic()
        {
            try
            {
                #if WINDOWS
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                if (_audioFileReader != null)
                {
                    _audioFileReader.Dispose();
                    _audioFileReader = null;
                }
                #endif

                if (_musicProcess != null)
                {
                    try
                    {
                        if (!_musicProcess.HasExited)
                        {
                            _musicProcess.Kill();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited, ignore
                    }

                    _musicProcess.Dispose();
                    _musicProcess = null;
                }

                _musicPausedByDeactivation = false;
                MusicStopped?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to stop launcher music", ex);
            }
        }

        public async Task FadeAsync(float targetVolume, int durationMs) => await FadeMusicAsync(targetVolume, durationMs);

        public async Task FadeMusicAsync(float targetVolume, int durationMs)
        {
            #if WINDOWS
            if (_audioFileReader == null)
                return;

            _fadeTaskCts?.Cancel();
            _fadeTaskCts = new CancellationTokenSource();
            var token = _fadeTaskCts.Token;

            try
            {
                float currentVolume = _audioFileReader.Volume;
                float targetVol = targetVolume;

                if (Math.Abs(currentVolume - targetVol) < 0.001f)
                    return;

                int steps = 20;
                int stepDelay = durationMs / steps;
                float volumeStep = (targetVol - currentVolume) / steps;

                for (int i = 0; i < steps; i++)
                {
                    if (token.IsCancellationRequested || _audioFileReader == null)
                        return;

                    currentVolume += volumeStep;
                    _audioFileReader.Volume = Math.Clamp(currentVolume, 0f, 1f);

                    await Task.Delay(stepDelay, token);
                }

                if (_audioFileReader != null && !token.IsCancellationRequested)
                {
                    _audioFileReader.Volume = targetVol;
                }
            }
            catch (OperationCanceledException)
            {
                // Fade was cancelled
            }
            catch (Exception ex)
            {
                Log.Error("Error during music fade", ex);
            }
            #else
            if (targetVolume < 0.01f)
            {
                if (_musicProcess != null && !_musicProcess.HasExited)
                {
                    try
                    {
                        _musicProcess.Kill();
                        Log.Debug("Music paused (process killed)");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Failed to pause music", ex);
                    }
                }
            }
            else if (targetVolume > 0.01f)
            {
                if (_musicProcess == null || _musicProcess.HasExited)
                {
                    if (!string.IsNullOrEmpty(MusicPath) && File.Exists(MusicPath))
                    {
                        PlayLauncherMusic(MusicPath);
                        Log.Debug("Music resumed");
                    }
                }
            }

            await Task.CompletedTask;
            #endif
        }

        // ── INotifyPropertyChanged ─────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

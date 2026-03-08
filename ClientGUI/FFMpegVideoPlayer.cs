using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClientCore;
using FFmpeg.AutoGen;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Rampastring.Tools;
using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;

using XnaRect = Microsoft.Xna.Framework.Rectangle;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace ClientGUI
{
    public sealed unsafe class FFMpegVideoPlayer : XNAControl, IDisposable
    {
        // ===== Video rendering =====
        private Texture2D _tex;
        private int _vw, _vh;            // decoded frame size

        private readonly ConcurrentQueue<VideoFrame> _videoQ = new();
        private VideoFrame _currentFrame;
        private double _lastPtsSec = 0.0;

        private sealed class VideoFrame
        {
            public byte[] RGBA;
            public int W, H;
            public double PtsSec;
        }

        // Tolerance (seconds) for frame-due check (~1 frame at 60 fps).
        private const double VIDEO_TOL = 0.016;

        // ===== Audio rendering =====
        private const int OUT_RATE = 48000;
        private const int OUT_CH = 2;
        private const int BYTES_PER_SAMPLE = 2; // s16
        private const int FRAGMENT_MS = 30;
        private const int INITIAL_PENDING = 2;
        private static readonly int FRAME_BYTES = OUT_RATE * OUT_CH * BYTES_PER_SAMPLE * FRAGMENT_MS / 1000;

        private DynamicSoundEffectInstance AudioObject;
        private volatile bool AudioActive;
        private readonly ConcurrentQueue<byte[]> PCMQueue = new();
        private byte[] FragmentBuffer = new byte[FRAME_BYTES];
        private byte[] _carry = Array.Empty<byte>();

        // Lock protecting _carry, FragmentBuffer and AudioObject submission
        // to prevent concurrent access from audio callback and UI thread.
        private readonly object _audioLock = new();

        // ===== FFmpeg context/state =====
        private AVFormatContext* FormatCtx;
        private AVCodecContext* _vCtx;
        private AVCodecContext* _aCtx;
        private SwsContext* _sws;
        private SwrContext* _swr;
        private AVFrame* _vFrame, _aFrame;
        private AVPacket* Packet;
        private AVRational _vTb;
        private AVRational _aTb;
        private int _vStream = -1;
        private int _aStream = -1;

        // PTS normalization per segment.
        private double? _vPts0 = null;

        private byte* _rgba;
        private int _stride;

        // ===== Demux/clock =====
        private Thread DemuxThread;
        private volatile bool _run;
        private volatile bool DemuxDone;
        private int _stopped;
        private readonly Stopwatch PlaybackClock = new();
        private double _seekBase;

        private bool _startPending = false;
        private bool _started = false;

        private int _decV;

        public event Action Completed;

        private bool _closing, _disposed;
        private bool _completedFired;

        // Looping coordination between UI and demux thread.
        private volatile bool _loopPending;
        private volatile bool _drewSinceEof;
        private readonly ManualResetEventSlim _seekEvent = new(false);

        private string MovieFilename = string.Empty;

        public bool Autoplay = true;
        public bool Looping = false;
        public bool IgnoreAudio = false;
        public bool ScaleToWindow = false;

        public enum ScaleMode { Stretch, Fit, Fill }
        public ScaleMode ScalingMode = ScaleMode.Stretch;

        public enum EndAction { Remove, Pause }
        public EndAction OnEnd = EndAction.Remove;

        public FFMpegVideoPlayer(WindowManager wm) : base(wm)
        {
            Name = "FFMpegVideoPlayer";
            DrawMode = ControlDrawMode.UNIQUE_RENDER_TARGET;
        }

        protected override void ParseControlINIAttribute(IniFile iniFile, string key, string value)
        {
            switch (key)
            {
                case "Movie":
                    MovieFilename = value;
                    return;

                case "Autoplay":
                    Autoplay = Conversions.BooleanFromString(value, true);
                    return;

                case "Looping":
                    Looping = Conversions.BooleanFromString(value, false);
                    return;

                case "IgnoreAudio":
                    IgnoreAudio = Conversions.BooleanFromString(value, false);
                    return;

                case "ScaleToWindow":
                    ScaleToWindow = Conversions.BooleanFromString(value, false);
                    return;

                case "Scaling":
                    if (!Enum.TryParse(value, true, out ScalingMode))
                    {
                        ScalingMode = ScaleMode.Fit;
                    }
                    return;

                case "EndState":
                    if (!Enum.TryParse(value, true, out OnEnd))
                    {
                        OnEnd = EndAction.Pause;
                    }
                    return;

                default:
                    base.ParseControlINIAttribute(iniFile, key, value);
                    return;
            }
        }

        public override void Initialize()
        {
            _startPending = true;
            base.Initialize();
        }

        // ---------- Public control API ----------

        public bool Open(string path, bool enableAudio = true, bool loops = false)
        {
            DemuxDone = false;
            _stopped = 0;
            _seekBase = 0;
            AudioActive = false;
            _completedFired = false;
            _decV = 0;

            _videoQ.Clear();
            _currentFrame = null;
            _vPts0 = null;
            _lastPtsSec = 0.0;

            Looping = loops;

            try
            {
                fixed (AVFormatContext** pFmt = &FormatCtx)
                {
                    int ret = ffmpeg.avformat_open_input(pFmt, path, null, null);
                    if (ret < 0)
                    {
                        throw new InvalidOperationException($"avformat_open_input failed ({FFErrStr(ret)}): {path}");
                    }
                }

                if (ffmpeg.avformat_find_stream_info(FormatCtx, null) < 0)
                {
                    throw new InvalidOperationException("avformat_find_stream_info failed.");
                }

                _vStream = ffmpeg.av_find_best_stream(FormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                _aStream = enableAudio ? ffmpeg.av_find_best_stream(FormatCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0)
                                       : -1;

                // Drop audio packets at the demux level when disabled.
                if (!enableAudio)
                {
                    for (int i = 0; i < FormatCtx->nb_streams; i++)
                    {
                        if (FormatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                        {
                            FormatCtx->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
                        }
                    }
                }

                // Open video codec.
                {
                    var vs = FormatCtx->streams[_vStream];
                    _vTb = vs->time_base;
                    var vdec = ffmpeg.avcodec_find_decoder(vs->codecpar->codec_id);
                    _vCtx = ffmpeg.avcodec_alloc_context3(vdec);
                    ffmpeg.avcodec_parameters_to_context(_vCtx, vs->codecpar);
                    if (ffmpeg.avcodec_open2(_vCtx, vdec, null) < 0)
                    {
                        throw new InvalidOperationException("avcodec_open2(video) failed.");
                    }

                    _vw = _vCtx->width; _vh = _vCtx->height; _stride = _vw * 4;
                    _sws = ffmpeg.sws_getContext(_vw, _vh, (AVPixelFormat)_vCtx->pix_fmt,
                                                 _vw, _vh, AVPixelFormat.AV_PIX_FMT_RGBA, ffmpeg.SWS_BILINEAR, null, null, null);
                    _rgba = (byte*)ffmpeg.av_malloc((ulong)(_stride * _vh));
                }

                // Open audio codec (optional).
                if (_aStream >= 0)
                {
                    var @as = FormatCtx->streams[_aStream];
                    _aTb = @as->time_base;
                    var adec = ffmpeg.avcodec_find_decoder(@as->codecpar->codec_id);
                    _aCtx = ffmpeg.avcodec_alloc_context3(adec);
                    ffmpeg.avcodec_parameters_to_context(_aCtx, @as->codecpar);
                    if (ffmpeg.avcodec_open2(_aCtx, adec, null) < 0)
                    {
                        throw new InvalidOperationException("avcodec_open2(audio) failed.");
                    }

                    AVChannelLayout outLayout = default; ffmpeg.av_channel_layout_default(&outLayout, OUT_CH);
                    AVChannelLayout inLayout = default;
                    if (_aCtx->ch_layout.nb_channels > 0)
                    {
                        ffmpeg.av_channel_layout_copy(&inLayout, &_aCtx->ch_layout);
                    }
                    else
                    {
                        ffmpeg.av_channel_layout_default(&inLayout, 2);
                    }

                    SwrContext* swrLocal = null;
                    int r = ffmpeg.swr_alloc_set_opts2(
                        &swrLocal,
                        &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OUT_RATE,
                        &inLayout, (AVSampleFormat)_aCtx->sample_fmt, _aCtx->sample_rate,
                        0, null);

                    ffmpeg.av_channel_layout_uninit(&inLayout);
                    ffmpeg.av_channel_layout_uninit(&outLayout);

                    if (r < 0 || swrLocal == null)
                    {
                        throw new InvalidOperationException("swr_alloc_set_opts2 failed.");
                    }
                    if (ffmpeg.swr_init(swrLocal) < 0)
                    {
                        ffmpeg.swr_free(&swrLocal);
                        throw new InvalidOperationException("swr_init failed.");
                    }

                    _swr = swrLocal;

                    AudioObject = new DynamicSoundEffectInstance(OUT_RATE, (AudioChannels)OUT_CH);
                    AudioObject.BufferNeeded += OnAudioBufferNeeded;
                }

                _vFrame = ffmpeg.av_frame_alloc();
                _aFrame = ffmpeg.av_frame_alloc();
                Packet = ffmpeg.av_packet_alloc();
            }
            catch
            {
                // Clean up any partially allocated FFmpeg resources on failure.
                CloseInternal();
                throw;
            }

            _run = true;
            DemuxThread = new Thread(DemuxLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "FFmpegDemux" };
            DemuxThread.Start();

            return true;
        }

        public bool Play()
        {
            if (_run && _aStream >= 0 && AudioObject != null)
            {
                lock (_audioLock)
                {
                    for (int i = 0; i < INITIAL_PENDING; i++)
                    {
                        FillFragment(FragmentBuffer);
                        AudioObject.SubmitBuffer(FragmentBuffer);
                    }
                }
                AudioActive = true;
                AudioObject.Play();
            }
            PlaybackClock.Restart();

            return true;
        }

        public void Pause()
        {
            if (_aStream >= 0 && AudioObject != null) AudioObject.Pause();
            PlaybackClock.Stop();
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

            _closing = true;
            AudioActive = false;
            if (AudioObject != null)
            {
                try { AudioObject.BufferNeeded -= OnAudioBufferNeeded; } catch { }
                try { AudioObject.Stop(); } catch { }
            }

            _run = false;

            // Unblock the demux thread if it's waiting for a seek signal.
            _seekEvent.Set();

            if (DemuxThread != null && Thread.CurrentThread != DemuxThread)
            {
                try { DemuxThread.Join(); } catch { }
            }

            Completed?.Invoke();
        }

        public override void Update(GameTime gameTime)
        {
            // Lazy-start: we need Initialize + GetINIAttributes to have run first.
            if (_startPending && !_started)
            {
                if (Autoplay && !string.IsNullOrEmpty(MovieFilename))
                {
                    try
                    {
                        var fileInfo = AssetLoaderExtensions.GetFile(MovieFilename);
                        if (fileInfo == null)
                            throw new FileNotFoundException($"Movie file '{MovieFilename}' not found in asset paths.");

                        Open(fileInfo.FullName, enableAudio: !IgnoreAudio, Looping);

                        // Auto-size if no explicit Size was set in INI.
                        if (Width == 0 || Height == 0)
                        {
                            // Fill parent so Scaling modes (Fit/Fill) work correctly.
                            if (Parent != null)
                                ClientRectangle = new XnaRect(0, 0, Parent.Width, Parent.Height);
                            else if (_vw > 0 && _vh > 0)
                                ClientRectangle = new XnaRect(X, Y, _vw, _vh);
                        }

                        Play();

                        _started = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"FFMpegVideoPlayer: Failed to start video '{MovieFilename}': {ex}");
                        Completed?.Invoke();
                        _started = false;
                    }
                }

                _startPending = false;
            }

            if (_closing || _disposed)
            {
                return;
            }

            // Wall-clock master for A/V sync.
            double master = _seekBase + (PlaybackClock.IsRunning ? PlaybackClock.Elapsed.TotalSeconds : 0.0);

            // Pull the latest frame that is due; skip earlier frames if we're behind.
            VideoFrame due = null;
            while (_videoQ.TryPeek(out var vf) && vf.PtsSec <= master + VIDEO_TOL)
            {
                _videoQ.TryDequeue(out due);
            }
            if (due != null)
            {
                if (_tex == null || _tex.Width != due.W || _tex.Height != due.H)
                {
                    _tex?.Dispose();
                    _tex = new Texture2D(GraphicsDevice, due.W, due.H, false, SurfaceFormat.Color);
                }
                _tex.SetData(due.RGBA);
                _currentFrame = due;
            }

            // Loop restart: must be checked BEFORE the EOF gate below, otherwise
            // the unconditional return in the EOF gate prevents this from ever executing.
            if (_loopPending && _drewSinceEof)
            {
                _loopPending = false;
                _drewSinceEof = false;

                // Clear DemuxDone from the UI thread so the EOF gate doesn't re-trigger
                // in the next frame before the demux thread has woken up.
                DemuxDone = false;

                // Signal demux thread to seek(0) and resume feeding.
                _seekEvent.Set();

                // Re-prime audio device for the new loop.
                if (_aStream >= 0 && AudioObject != null)
                {
                    lock (_audioLock)
                    {
                        _carry = Array.Empty<byte>();
                        for (int i = 0; i < INITIAL_PENDING; i++)
                        {
                            FillFragment(FragmentBuffer);
                            AudioObject.SubmitBuffer(FragmentBuffer);
                        }
                    }
                    AudioActive = true;
                    AudioObject.Play();
                }

                _seekBase = 0.0;
                PlaybackClock.Restart();
                return;
            }

            // EOF gate: demux finished AND audio drained AND no queued video.
            bool noMoreVideo = _videoQ.IsEmpty;
            bool audioDrained = (AudioObject == null || AudioObject.PendingBufferCount == 0);
            if (DemuxDone && noMoreVideo && audioDrained)
            {
                if (Looping)
                {
                    // Defer restart until Draw() has presented the final frame at least once.
                    _loopPending = true;
                }
                else
                {
                    switch (OnEnd)
                    {
                        // Freeze on last frame: stop the clock and audio, keep texture alive.
                        case EndAction.Pause:
                            if (!_completedFired)
                            {
                                _completedFired = true;
                                AudioActive = false;
                                PlaybackClock.Stop();
                                Completed?.Invoke();
                            }
                            break;

                        case EndAction.Remove:
                            Stop();
                            break;
                    }
                }
                return;
            }
        }

        public override void Draw(GameTime gameTime)
        {
            if (_closing || _disposed)
            {
                return;
            }
            if (_tex == null)
            {
                return;
            }

            XnaRect dest = ScaleToWindow ? new XnaRect(0, 0, Width, Height) : XnaRect.Intersect(ClientRectangle, new XnaRect(0, 0, Width, Height));

            if (dest.Width <= 0 || dest.Height <= 0)
            {
                return;
            }

            XnaRect draw = dest;
            if (ScalingMode != ScaleMode.Stretch)
            {
                float sx = (float)dest.Width / _tex.Width;
                float sy = (float)dest.Height / _tex.Height;
                float s = (ScalingMode == ScaleMode.Fit) ? MathF.Min(sx, sy) : MathF.Max(sx, sy);
                int dw = Math.Max(1, (int)(_tex.Width * s));
                int dh = Math.Max(1, (int)(_tex.Height * s));
                int dx = dest.X + (dest.Width - dw) / 2;
                int dy = dest.Y + (dest.Height - dh) / 2;
                draw = new XnaRect(dx, dy, dw, dh);
            }

            Renderer.DrawTexture(_tex, draw, XnaColor.White);

            if (_loopPending)
            {
                _drewSinceEof = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (disposing)
            {
                try { Stop(); } catch { }

                _tex?.Dispose(); _tex = null;

                CloseInternal();

                AudioObject?.Dispose(); AudioObject = null;

                _seekEvent.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- Demux / Decode ----------
        private void DemuxLoop()
        {
            for (; ; )
            {
                if (!_run)
                {
                    break;
                }

                int ret = ffmpeg.av_read_frame(FormatCtx, Packet);
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    if (Looping)
                    {
                        // Signal EOF to UI; do NOT seek yet -- let UI show last frame & drain audio.
                        DemuxDone = true;

                        // Block until UI (or Stop) signals us to seek.
                        _seekEvent.Wait();
                        _seekEvent.Reset();

                        if (!_run) break;

                        // UI requested restart; perform seek-to-start.
                        DemuxDone = false;
                        Seek(TimeSpan.Zero);
                        ffmpeg.av_packet_unref(Packet);
                        continue;
                    }

                    // Non-looping: flush video decoder for any remaining frames.
                    if (_vCtx != null)
                    {
                        ffmpeg.avcodec_send_packet(_vCtx, null);
                        while (ffmpeg.avcodec_receive_frame(_vCtx, _vFrame) == 0)
                        {
                            EnqueueVideoFrame();
                            ffmpeg.av_frame_unref(_vFrame);
                        }
                    }

                    // Drain SWR (audio resampler) for trailing samples.
                    if (_aCtx != null && _swr != null)
                    {
                        // Hoist stackalloc out of loop to avoid CA2014 (potential stack overflow).
                        byte** outData = stackalloc byte*[1];
                        for (; ; )
                        {
                            int outLine;
                            outData[0] = null;
                            int dst = FRAME_BYTES / (OUT_CH * BYTES_PER_SAMPLE);
                            if (ffmpeg.av_samples_alloc(outData, &outLine, OUT_CH, dst, AVSampleFormat.AV_SAMPLE_FMT_S16, 0) < 0)
                            {
                                break;
                            }

                            int got = ffmpeg.swr_convert(_swr, outData, dst, null, 0);
                            if (got <= 0)
                            {
                                ffmpeg.av_freep(outData);
                                break;
                            }

                            int bytes = got * OUT_CH * BYTES_PER_SAMPLE;
                            var pcm = new byte[bytes];
                            Marshal.Copy((IntPtr)outData[0], pcm, 0, bytes);
                            ffmpeg.av_freep(outData);

                            PCMQueue.Enqueue(pcm);
                        }
                    }

                    DemuxDone = true;
                    break;
                }
                if (ret < 0)
                {
                    ffmpeg.av_packet_unref(Packet);
                    continue;
                }

                if (Packet->stream_index == _vStream)
                {
                    if (ffmpeg.avcodec_send_packet(_vCtx, Packet) == 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(_vCtx, _vFrame) == 0)
                        {
                            EnqueueVideoFrame();
                            ffmpeg.av_frame_unref(_vFrame);
                        }
                    }
                }
                else if (_aStream >= 0 && Packet->stream_index == _aStream)
                {
                    if (ffmpeg.avcodec_send_packet(_aCtx, Packet) == 0)
                    {
                        // Hoist stackalloc out of loop to avoid CA2014 (potential stack overflow).
                        byte** outData = stackalloc byte*[1];
                        while (ffmpeg.avcodec_receive_frame(_aCtx, _aFrame) == 0)
                        {
                            int delay = (int)ffmpeg.swr_get_delay(_swr, _aCtx->sample_rate);
                            int dst = (int)ffmpeg.av_rescale_rnd(delay + _aFrame->nb_samples, OUT_RATE, _aCtx->sample_rate, AVRounding.AV_ROUND_UP);

                            int outLine;
                            outData[0] = null;
                            if (ffmpeg.av_samples_alloc(outData, &outLine, OUT_CH, dst, AVSampleFormat.AV_SAMPLE_FMT_S16, 0) < 0)
                            {
                                ffmpeg.av_frame_unref(_aFrame);
                                break;
                            }

                            int got = ffmpeg.swr_convert(_swr, outData, dst, _aFrame->extended_data, _aFrame->nb_samples);
                            if (got > 0)
                            {
                                int bytes = got * OUT_CH * BYTES_PER_SAMPLE;
                                var pcm = new byte[bytes];
                                Marshal.Copy((IntPtr)outData[0], pcm, 0, bytes);
                                PCMQueue.Enqueue(pcm);
                            }
                            ffmpeg.av_freep(outData);
                            ffmpeg.av_frame_unref(_aFrame);
                        }
                    }
                }

                ffmpeg.av_packet_unref(Packet);
            }
        }

        private void EnqueueVideoFrame()
        {
            if (_rgba == null)
            {
                return;
            }

            byte_ptrArray4 dst = default; int_array4 ls = default;
            ls[0] = _stride; dst[0] = _rgba;

            int scaled = ffmpeg.sws_scale(_sws, _vFrame->data, _vFrame->linesize, 0, _vh, dst, ls);
            if (scaled <= 0)
            {
                return;
            }

            int sz = _stride * _vh;
            var managed = new byte[sz];
            Marshal.Copy((IntPtr)_rgba, managed, 0, sz);

            // Compute PTS in seconds (normalized to segment start).
            double ptsAbs;
            long ts = _vFrame->best_effort_timestamp;
            if (ts != ffmpeg.AV_NOPTS_VALUE)
            {
                ptsAbs = ts * ffmpeg.av_q2d(_vTb);
            }
            else
            {
                // Fallback: derive synthetic PTS from framerate.
                double fps = ffmpeg.av_q2d(_vCtx->framerate);
                if (fps <= 0.0) fps = 30.0;
                ptsAbs = (_vPts0 ?? 0.0) + (1.0 / fps) + _lastPtsSec;
            }

            if (_vPts0 == null) _vPts0 = ptsAbs;

            double ptsNorm = ptsAbs - _vPts0.Value;
            _lastPtsSec = ptsNorm;

            _videoQ.Enqueue(new VideoFrame { RGBA = managed, W = _vw, H = _vh, PtsSec = ptsNorm });

            _decV++;
            if (_decV % 30 == 1) Debug.WriteLine($"[FFMpegVideoPlayer] Decoded {_decV} frames {_vw}x{_vh} pts={_lastPtsSec:F3}");
        }

        private void Seek(TimeSpan pos)
        {
            double s = pos.TotalSeconds;
            long vts = (long)(s / ffmpeg.av_q2d(_vTb));
            ffmpeg.av_seek_frame(FormatCtx, _vStream, vts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_vCtx);

            if (_aStream >= 0)
            {
                long ats = (long)(s / ffmpeg.av_q2d(_aTb));
                ffmpeg.av_seek_frame(FormatCtx, _aStream, ats, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_aCtx);

                // Drain SWR to discard stale resampler state from the previous segment.
                if (_swr != null)
                {
                    ffmpeg.swr_convert(_swr, null, 0, null, 0);
                }
            }

            // Clear queued video/audio but keep _currentFrame and _tex alive so
            // Draw() continues showing the last frame until the first new frame
            // arrives from the demux thread. This prevents a black flash at the
            // loop boundary.
            _videoQ.Clear();
            _vPts0 = null;
            _lastPtsSec = 0.0;
            PCMQueue.Clear();
            _seekBase = s;
            PlaybackClock.Restart();
        }

        private void OnAudioBufferNeeded(object sender, EventArgs e)
        {
            if (!AudioActive || AudioObject == null)
            {
                return;
            }

            // Stop feeding once PCM is fully drained at EOF; let device run to zero.
            if (DemuxDone && PCMQueue.IsEmpty && _carry.Length == 0)
            {
                AudioActive = false;
                return;
            }

            try
            {
                lock (_audioLock)
                {
                    FillFragment(FragmentBuffer);
                    AudioObject.SubmitBuffer(FragmentBuffer);
                }
            }
            catch (InvalidOperationException) { }
            catch { }
        }

        // Fill exactly FRAME_BYTES into dst from queue/carry; pad with zeros if short.
        private void FillFragment(byte[] dst)
        {
            int filled = 0;

            // Drain carry buffer first.
            if (_carry.Length > 0)
            {
                int take = Math.Min(_carry.Length, FRAME_BYTES);
                Buffer.BlockCopy(_carry, 0, dst, 0, take); filled += take;
                if (take < _carry.Length)
                {
                    int remain = _carry.Length - take;
                    Buffer.BlockCopy(_carry, take, _carry, 0, remain);
                    Array.Resize(ref _carry, remain);
                }
                else
                {
                    _carry = Array.Empty<byte>();
                }
            }

            // Drain PCM queue.
            while (filled < FRAME_BYTES && PCMQueue.TryDequeue(out var chunk))
            {
                int copy = Math.Min(chunk.Length, FRAME_BYTES - filled);
                Buffer.BlockCopy(chunk, 0, dst, filled, copy);
                filled += copy;

                if (copy < chunk.Length)
                {
                    int remain = chunk.Length - copy;
                    _carry = new byte[remain];
                    Buffer.BlockCopy(chunk, copy, _carry, 0, remain);
                }
            }

            // Zero-pad any shortfall.
            if (filled < FRAME_BYTES)
            {
                Array.Clear(dst, filled, FRAME_BYTES - filled);
            }
        }

        private void CloseInternal()
        {
            if (_rgba != null) { ffmpeg.av_free(_rgba); _rgba = null; }

            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_swr != null) { fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p); _swr = null; }

            if (_vCtx != null) { fixed (AVCodecContext** p = &_vCtx) ffmpeg.avcodec_free_context(p); _vCtx = null; }
            if (_aCtx != null) { fixed (AVCodecContext** p = &_aCtx) ffmpeg.avcodec_free_context(p); _aCtx = null; }

            if (_vFrame != null) { fixed (AVFrame** p = &_vFrame) ffmpeg.av_frame_free(p); _vFrame = null; }
            if (_aFrame != null) { fixed (AVFrame** p = &_aFrame) ffmpeg.av_frame_free(p); _aFrame = null; }
            if (Packet != null) { fixed (AVPacket** p = &Packet) ffmpeg.av_packet_free(p); Packet = null; }

            if (FormatCtx != null) { fixed (AVFormatContext** p = &FormatCtx) ffmpeg.avformat_close_input(p); FormatCtx = null; }
        }

        private static string FFErrStr(int err)
        {
            const int SZ = 256;
            byte* buf = stackalloc byte[SZ];
            ffmpeg.av_strerror(err, buf, (ulong)SZ);
            return Marshal.PtrToStringAnsi((IntPtr)buf) ?? err.ToString();
        }
    }
}

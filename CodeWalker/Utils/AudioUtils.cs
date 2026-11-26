using CodeWalker.GameFiles;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeWalker.Utils
{



    public class AudioPlayer
    {
        private XAudio2 xAudio2;
        private MasteringVoice masteringVoice;

        public class AudioVoice
        {
            public AwcStream audio;
            public object key; //what is loaded: the AwcStream, or the GTA VI sound given to LoadWav
            public SoundStream soundStream;
            public AudioBuffer audioBuffer;
            public SourceVoice sourceVoice;
            public float[] outputMatrix = new[] { 1.0f, 1.0f }; //left/right channel output levels
            public float trackLength;
            public PcmFeed feed; //streamed sound (LoadPcmStream): played from here instead of audioBuffer
        }

        /// <summary>PCM decoded on a background thread for a streamed sound; pieces are appended as they come.</summary>
        public class PcmFeed
        {
            public object Key;
            public int Rate, Channels;
            public readonly List<byte[]> Pieces = new List<byte[]>();
            public long Bytes;
            public volatile bool Done, Cancel;
            public volatile string Error;
            public double Seconds { get { lock (Pieces) return Bytes / (2.0 * Channels * Rate); } }
        }
        private PcmFeed feed;
        private int feedGen;
        private readonly object feedLock = new object();

        /// <summary>Error from the background decoder of the loaded streamed sound, if any.</summary>
        public string FeedError => feed?.Error;
        /// <summary>A streamed sound is still being decoded.</summary>
        public bool FeedDecoding => (feed != null) && !feed.Done;
        private AudioVoice[] voices = new AudioVoice[0];

        public enum PlayerState { Stopped, Playing, Paused };
        public PlayerState State { get; private set; } = PlayerState.Stopped;

        private Stopwatch playtimer = new Stopwatch();
        private int playBeginMs;
        private float trackLength;
        public bool trackFinished;

        private float volume = 1.0f;

        public int PlayTimeMS
        {
            get
            {
                return (int)playtimer.Elapsed.TotalMilliseconds + playBeginMs;
            }
        }
        public int TotalTimeMS
        {
            get
            {
                return (int)(trackLength * 1000);
            }
        }


        public void LoadAudio(params AwcStream[] audios)
        {
            if (xAudio2 == null)
            {
                xAudio2 = new XAudio2();
                masteringVoice = new MasteringVoice(xAudio2);
            }

            if ((voices == null) || (voices.Length != audios.Length))
            {
                voices = new AudioVoice[audios.Length];
                for (int i = 0; i < audios.Length; i++)
                {
                    voices[i] = new AudioVoice();
                }
            }

            trackLength = 0;
            for (int i = 0; i < audios.Length; i++)
            {
                var voice = voices[i];
                var audio = audios[i];
                if ((audio != voice.audio) || (voice.key != (object)audio))
                {
                    voice.feed = null;
                    voice.audio = audio;
                    voice.key = audio;
                    voice.trackLength = audio.Length;
                    trackLength = Math.Max(trackLength, voice.trackLength);
                    var wavStream = audio.GetWavStream();
                    var soundStream = new SoundStream(wavStream);
                    voice.soundStream = soundStream;
                    voice.audioBuffer = new AudioBuffer
                    {
                        Stream = soundStream.ToDataStream(),
                        AudioBytes = (int)soundStream.Length,
                        Flags = BufferFlags.EndOfStream
                    };
                    soundStream.Close();
                    wavStream.Close();
                }
            }

        }

        /// <summary>Load one already decoded sound (a whole .wav file, mono or stereo). getWav runs only when key
        /// isn't the sound loaded now.</summary>
        public void LoadWav(object key, Func<byte[]> getWav, float length)
        {
            if (xAudio2 == null)
            {
                xAudio2 = new XAudio2();
                masteringVoice = new MasteringVoice(xAudio2);
            }
            if ((voices == null) || (voices.Length != 1))
            {
                voices = new[] { new AudioVoice() };
            }
            var voice = voices[0];
            if (voice.key != key)
            {
                var wav = getWav();
                voice.audioBuffer?.Stream?.Dispose();
                voice.feed = null;
                voice.audio = null;
                voice.key = key;
                voice.trackLength = length;
                using (var ms = new MemoryStream(wav))
                {
                    var soundStream = new SoundStream(ms);
                    voice.soundStream = soundStream;
                    voice.audioBuffer = new AudioBuffer
                    {
                        Stream = soundStream.ToDataStream(),
                        AudioBytes = (int)soundStream.Length,
                        Flags = BufferFlags.EndOfStream
                    };
                    soundStream.Close();
                }
            }
            voice.outputMatrix = new[] { 1.0f, 1.0f };
            trackLength = voice.trackLength;
        }

        /// <summary>Load a sound that is decoded on a background thread while it plays: decode yields 16-bit
        /// interleaved PCM pieces. Decoding starts only when key isn't the sound loaded now, and the decoded PCM is
        /// kept, so replaying and seeking don't decode again.</summary>
        public void LoadPcmStream(object key, int rate, int channels, float length, Func<IEnumerable<byte[]>> decode)
        {
            if (xAudio2 == null)
            {
                xAudio2 = new XAudio2();
                masteringVoice = new MasteringVoice(xAudio2);
            }
            if ((voices == null) || (voices.Length != 1))
            {
                voices = new[] { new AudioVoice() };
            }
            if ((feed == null) || (feed.Key != key))
            {
                if (feed != null) feed.Cancel = true;
                var f = new PcmFeed { Key = key, Rate = rate, Channels = channels };
                feed = f;
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        foreach (var b in decode())
                        {
                            if (f.Cancel) return;
                            lock (f.Pieces) { f.Pieces.Add(b); f.Bytes += b.Length; }
                        }
                    }
                    catch (Exception ex) { f.Error = ex.Message; }
                    finally { f.Done = true; }
                });
                t.IsBackground = true;
                t.Name = "AWC decode";
                t.Start();
            }
            var voice = voices[0];
            if (voice.feed != feed)
            {
                voice.audioBuffer?.Stream?.Dispose();
                voice.audioBuffer = null;
                voice.soundStream = null;
                voice.audio = null;
            }
            voice.key = key;
            voice.feed = feed;
            voice.trackLength = length;
            voice.outputMatrix = new[] { 1.0f, 1.0f };
            trackLength = length;
        }

        /// <summary>Feeds a streamed voice from byte position pos: keeps a few buffers queued as the decoder
        /// produces them, and marks the track finished once everything decoded has played.</summary>
        private void StartFeeder(AudioVoice voice, long pos)
        {
            int gen = feedGen;
            var sv = voice.sourceVoice;
            var f = voice.feed;
            var t = new System.Threading.Thread(() =>
            {
                var held = new Queue<SharpDX.DataStream>();
                int idx = 0; long at = 0;
                while (true)
                {
                    bool finished = false;
                    lock (feedLock)
                    {
                        if (gen != feedGen) break; //voice destroyed: its buffers are free
                        int queued = sv.State.BuffersQueued;
                        while (held.Count > queued) held.Dequeue().Dispose();
                        while (queued < 6)
                        {
                            byte[] piece = null;
                            lock (f.Pieces)
                            {
                                while ((idx < f.Pieces.Count) && (at + f.Pieces[idx].Length <= pos)) { at += f.Pieces[idx].Length; idx++; }
                                if (idx < f.Pieces.Count)
                                {
                                    var p = f.Pieces[idx];
                                    int off = (int)(pos - at);
                                    if (off == 0) piece = p;
                                    else { piece = new byte[p.Length - off]; Buffer.BlockCopy(p, off, piece, 0, piece.Length); }
                                    pos = at + p.Length;
                                }
                            }
                            if (piece == null) break;
                            var ds = SharpDX.DataStream.Create(piece, true, false);
                            held.Enqueue(ds);
                            sv.SubmitSourceBuffer(new AudioBuffer { Stream = ds, AudioBytes = piece.Length, Flags = BufferFlags.None }, null);
                            queued++;
                        }
                        if (f.Done && (queued == 0))
                        {
                            bool all;
                            lock (f.Pieces) all = pos >= f.Bytes;
                            finished = all;
                        }
                    }
                    if (finished) { trackFinished = true; break; }
                    System.Threading.Thread.Sleep(15);
                }
                //either the voice is destroyed, or it finished with nothing queued: no buffer is in use
                lock (feedLock)
                {
                    while (held.Count > 0) held.Dequeue().Dispose();
                }
            });
            t.IsBackground = true;
            t.Name = "AWC feed";
            t.Start();
        }

        private static int VoiceChannels(AudioVoice voice)
        {
            return (voice.feed != null) ? voice.feed.Channels : voice.soundStream.Format.Channels;
        }

        private void ApplyOutputMatrix(AudioVoice voice)
        {
            if (VoiceChannels(voice) == 2)
            {
                voice.sourceVoice.SetOutputMatrix(2, 2, new[] { 1.0f, 0.0f, 0.0f, 1.0f });
            }
            else
            {
                voice.sourceVoice.SetOutputMatrix(1, 2, voice.outputMatrix);
            }
        }

        private void CreateSourceVoices(float playBegin = 0)
        {
            foreach (var voice in voices)
            {
                var f = voice.feed;
                if ((f != null) && !f.Done) playBegin = Math.Min(playBegin, (float)f.Seconds); //can't seek past what's decoded yet
            }
            if (playBegin > 0)
            {
                foreach (var voice in voices)
                {
                    if (voice.feed != null) continue;
                    voice.audioBuffer.PlayBegin = (int)(voice.soundStream.Format.SampleRate * playBegin) / 128 * 128;
                }
                if (playtimer.IsRunning)
                {
                    playtimer.Restart();
                }
                else
                {
                    playtimer.Reset();
                }
                playBeginMs = (int)(playBegin * 1000);
            }
            else
            {
                playBeginMs = 0;
            }

            trackFinished = false;
            foreach (var voice in voices)
            {
                var f = voice.feed;
                if (f != null)
                {
                    var sv = new SourceVoice(xAudio2, new WaveFormat(f.Rate, 16, f.Channels), true);
                    sv.SetVolume(volume);
                    voice.sourceVoice = sv;
                    ApplyOutputMatrix(voice);
                    int align = 2 * f.Channels;
                    StartFeeder(voice, (long)(playBegin * f.Rate) * align);
                    continue;
                }
                var sourceVoice = new SourceVoice(xAudio2, voice.soundStream.Format, true);
                sourceVoice.SubmitSourceBuffer(voice.audioBuffer, voice.soundStream.DecodedPacketsInfo);
                sourceVoice.BufferEnd += (context) => trackFinished = true;
                sourceVoice.SetVolume(volume);
                voice.sourceVoice = sourceVoice;
                ApplyOutputMatrix(voice);
            }
        }

        private void SetPlayerState(PlayerState newState)
        {
            if (State != newState)
            {
                switch (newState)
                {
                    case PlayerState.Playing:
                        if (State == PlayerState.Stopped)
                        {
                            playtimer.Reset();
                        }
                        playtimer.Start();
                        break;
                    case PlayerState.Paused:
                        playtimer.Stop();
                        break;
                    case PlayerState.Stopped:
                        playtimer.Stop();
                        break;
                }

                State = newState;
            }
        }

        public void SetVolume(float v)
        {
            volume = v;
            if (State == PlayerState.Playing)
            {
                foreach (var voice in voices)
                {
                    voice.sourceVoice.SetVolume(v);
                }
            }
        }

        public void SetOutputMatrix(int v, float l, float r)
        {
            var voice = voices[v];
            voice.outputMatrix[0] = l;
            voice.outputMatrix[1] = r;
            if (State == PlayerState.Playing)
            {
                ApplyOutputMatrix(voice);
            }
        }

        public void DisposeAudio()
        {
            if (feed != null) feed.Cancel = true;
            if (xAudio2 != null)
            {
                masteringVoice.Dispose();
                xAudio2.Dispose();
            }
            foreach (var voice in voices)
            {
                voice?.audioBuffer?.Stream?.Dispose();
            }
        }


        public void Stop()
        {
            if (State != PlayerState.Stopped)
            {
                lock (feedLock) //a feeder thread may be submitting buffers to these voices
                {
                    feedGen++;
                    foreach (var voice in voices)
                    {
                        voice.sourceVoice.DestroyVoice();
                        voice.sourceVoice.Dispose();
                    }
                }
                SetPlayerState(PlayerState.Stopped);
            }
        }

        public void Play(float playBegin = 0)
        {
            Stop();
            CreateSourceVoices(playBegin);
            foreach (var voice in voices)
            {
                voice.sourceVoice.Start();
            }
            SetPlayerState(PlayerState.Playing);
        }

        public void Seek(float playBegin = 0)
        {
            if (State == PlayerState.Playing)
            {
                Play(playBegin);
            }
            else if (State == PlayerState.Paused)
            {
                var state = State;
                Stop();
                CreateSourceVoices(playBegin);
                State = state;
            }
        }

        public void Pause()
        {
            if (State == PlayerState.Playing)
            {
                foreach (var voice in voices)
                {
                    voice.sourceVoice.Stop();
                }
                SetPlayerState(PlayerState.Paused);
            }
        }

        public void Resume()
        {
            if (State == PlayerState.Paused)
            {
                foreach (var voice in voices)
                {
                    voice.sourceVoice.Start();
                }
                SetPlayerState(PlayerState.Playing);
            }
        }

    }



    public class AudioDatabase
    {

        public bool IsInited { get; set; }

        public Dictionary<uint, Dat54Sound> SoundsDB { get; set; }
        public Dictionary<uint, Dat151RelData> GameDB { get; set; }
        public Dictionary<uint, RpfFileEntry> ContainerDB { get; set; }

        public void Init(GameFileCache gameFileCache, bool sounds = true, bool game = true)
        {


            var rpfman = gameFileCache.RpfMan;

            var datrelentries = new Dictionary<uint, RpfFileEntry>();
            var awcentries = new Dictionary<uint, RpfFileEntry>();
            void addRpfDatRels(RpfFile rpffile)
            {
                if (rpffile.AllEntries == null) return;
                foreach (var entry in rpffile.AllEntries)
                {
                    if (entry is RpfFileEntry)
                    {
                        var fentry = entry as RpfFileEntry;
                        //if (entry.NameLower.EndsWith(".rel"))
                        //{
                        //    datrels[entry.NameHash] = fentry;
                        //}
                        if (sounds && entry.NameLower.EndsWith(".dat54.rel"))
                        {
                            datrelentries[entry.NameHash] = fentry;
                        }
                        if (game && entry.NameLower.EndsWith(".dat151.rel"))
                        {
                            datrelentries[entry.NameHash] = fentry;
                        }
                    }
                }
            }
            void addRpfAwcs(RpfFile rpffile)
            {
                if (rpffile.AllEntries == null) return;
                foreach (var entry in rpffile.AllEntries)
                {
                    if (entry is RpfFileEntry)
                    {
                        var fentry = entry as RpfFileEntry;
                        if (entry.NameLower.EndsWith(".awc"))
                        {
                            var shortname = entry.GetShortNameLower();
                            var parentname = entry.Parent?.GetShortNameLower() ?? "";
                            if (string.IsNullOrEmpty(parentname) && (entry.Parent?.File != null))
                            {
                                parentname = entry.Parent.File.NameLower;
                                int ind = parentname.LastIndexOf('.');
                                if (ind > 0)
                                {
                                    parentname = parentname.Substring(0, ind);
                                }
                            }
                            var contname = parentname + "/" + shortname;
                            var hash = JenkHash.GenHash(contname);
                            awcentries[hash] = fentry;
                        }
                    }
                }
            }

            var audrpf = rpfman.FindRpfFile("x64\\audio\\audio_rel.rpf");
            if (audrpf != null)
            {
                addRpfDatRels(audrpf);
            }
            foreach (var baserpf in gameFileCache.BaseRpfs)
            {
                addRpfAwcs(baserpf);
            }
            if (gameFileCache.EnableDlc)
            {
                var updrpf = rpfman.FindRpfFile("update\\update.rpf");
                if (updrpf != null)
                {
                    addRpfDatRels(updrpf);
                }
                foreach (var dlcrpf in gameFileCache.DlcActiveRpfs) //load from current dlc rpfs
                {
                    addRpfDatRels(dlcrpf);
                    addRpfAwcs(dlcrpf);
                }
            }



            var soundsdb = new Dictionary<uint, Dat54Sound>();
            var gamedb = new Dictionary<uint, Dat151RelData>();
            foreach (var datentry in datrelentries.Values)
            {
                var relfile = rpfman.GetFile<RelFile>(datentry);
                if (relfile?.RelDatas != null)
                {
                    foreach (var rd in relfile.RelDatas)
                    {
                        if (rd is Dat54Sound sd)
                        {
                            soundsdb[sd.NameHash] = sd;
                        }
                        else if (rd is Dat151RelData gd)
                        {
                            gamedb[gd.NameHash] = gd;
                        }
                    }
                }
            }

            ContainerDB = awcentries;
            if (sounds) SoundsDB = soundsdb;
            if (game) GameDB = gamedb;

            IsInited = true;
        }


    }


}

﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using G3SDK;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TobiiGlassesManager.Core;
using Unosquare.FFME;
using Unosquare.FFME.Common;
using Timer = System.Timers.Timer;

namespace TobiiGlassesManager.MVVM.ViewModels
{
    internal class RecordingViewModel : ObservableObject, IProgress<double>
    {
        public static async Task<RecordingViewModel> Create(Dispatcher d, IRecording r, IG3Api g3)
        {
            var vm = new RecordingViewModel(d, r, g3);
            await vm.Init();
            return vm;
        }

        private readonly IRecording _recording;
        private TimeSpan _duration;
        private string _visibleName;
        private DateTime _created;
        private readonly List<G3GazeData> _gaze = new List<G3GazeData>();
        private ConcurrentQueue<G3GazeData> _gazeQueue;
        private System.Windows.Controls.MediaElement _media;
        private TimeSpan _position;
        private double _gazeLoadedUntil;
        private Timer _gazeLoadTimer;
        private double _gazeMarkerSize = 20;
        private double _gazeY;
        private double _gazeX;
        private readonly IComparer<G3GazeData> _timeStampComparer = new TimeStampComparer();
        private bool _deviceIsRecording;
        private bool _rtaInProgress;
        private readonly IG3Api _g3;
        private string _thumbnail = "images/image-not-found.png";
        private string _huSerial;
        private string _ruSerial;
        private string _fwVersion;
        private bool _isPlaying;
        private double _downloadProgress;

        private RecordingViewModel(Dispatcher dispatcher, IRecording recording, IG3Api g3) : base(dispatcher)
        {
            _recording = recording;
            _g3 = g3;
            TogglePlay = new RelayCommand(async o => await DoTogglePlay());

            DeleteRecording = new RelayCommand(async o => await DoDeleteRecording());
            Download = new RelayCommand(async o => await DoDownload());

            _g3.Recorder.Started.SubscribeAsync(g => DeviceIsRecording = true);
            _g3.Recorder.Stopped.SubscribeAsync(g => DeviceIsRecording = false);
        }

        private async Task DoDownload()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                DialogResult result = fbd.ShowDialog();

                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    var d = new RecordingDownloader(_g3);
                    await d.DownloadRecording(_recording, fbd.SelectedPath, this);
                }
            }
        }

        private Task DoDeleteRecording()
        {
            return _g3.Recordings.Delete(_recording.UUID);
        }

        private async Task SendRtaInfo()
        {
            if (_rtaInProgress)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var info = new RtaInfo(_media.Position, _isPlaying);
                    _g3.Recorder.SendEvent("RTA", info);
                });
            }
        }

        public bool RtaInProgress
        {
            get => _rtaInProgress;
            set
            {
                if (value == _rtaInProgress) return;
                _rtaInProgress = value;
                OnPropertyChanged();
                RaiseCanExecuteChange(StopRTA);
            }
        }

        public bool DeviceIsRecording
        {
            get => _deviceIsRecording;
            set
            {
                if (value == _deviceIsRecording) return;
                _deviceIsRecording = value;
                OnPropertyChanged();
                RaiseCanExecuteChange(StartRTA);
            }
        }

        public Task UpdateMediaPlayerData()
        {
            InternalSetPosition(_media.Position);
            RenderGazeData(_media.Position);

            return Task.CompletedTask;
        }

        public async Task AttachMediaPlayer(System.Windows.Controls.MediaElement media)
        {
            _media = media;

            _media.BufferingStarted += (sender, args) =>
            {
                InternalSetPosition(_media.Position);
                RenderGazeData(_media.Position);
            };

            await PrepareReplay();
        }

        private MediaState GetMediaState(System.Windows.Controls.MediaElement myMedia)
        {
            FieldInfo hlp = typeof(System.Windows.Controls.MediaElement).GetField("_helper", BindingFlags.NonPublic | BindingFlags.Instance);
            object helperObject = hlp.GetValue(myMedia);
            FieldInfo stateField = helperObject.GetType().GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
            MediaState state = (MediaState)stateField.GetValue(helperObject);
            return state;
        }

        private void RenderGazeData(TimeSpan timeStamp)
        {
            var g3GazeData = new G3GazeData(timeStamp, Vector2Extensions.INVALID, Vector3Extensions.INVALID, null, null);
            var index = _gaze.BinarySearch(g3GazeData, _timeStampComparer);
            if (index < 0)
                index = -index;
            if (index < _gaze.Count)
            {
                var gaze2D = _gaze[index].Gaze2D;
                if (gaze2D.IsValid() && gaze2D.X >= 0 && gaze2D.X <= 1 && gaze2D.Y >= 0 && gaze2D.Y <= 1)
                {
                    GazeX = gaze2D.X * _media.ActualWidth - GazeMarkerSize / 2;
                    GazeY = gaze2D.Y * _media.ActualHeight - GazeMarkerSize / 2;
                    return;
                }
            }
            GazeX = int.MinValue;
            GazeY = int.MinValue;
        }

        private void InternalSetPosition(TimeSpan t)
        {
            _position = t;
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(PositionInSeconds));
        }

        private async Task DoTogglePlay()
        {
            if (_gaze == null)
                await PrepareReplay();

            if (GetMediaState(_media).Equals(MediaState.Play))
            {
                _media.Pause();
                _isPlaying = false;
            }
            else
            {
                _media.Play();
                _isPlaying = true;

            }
            await SendRtaInfo();
        }

        private async Task Init()
        {
            Duration = await _recording.Duration;
            VisibleName = await _recording.VisibleName;
            DeviceIsRecording = (await _g3.Recorder.Duration).HasValue;
            Created = await _recording.Created;
            FwVersion = await _recording.MetaLookupString(MetaDataCapableHelpers.MetaDataKey_RuVersion);
            RuSerial = await _recording.MetaLookupString(MetaDataCapableHelpers.MetaDataKey_RuSerial);
            HuSerial = await _recording.MetaLookupString(MetaDataCapableHelpers.MetaDataKey_HuSerial);
            VideoUri = await _recording.GetUri("scenevideo.mp4");
            var json = await _recording.GetRecordingJson();
            var snapshotarr = (JArray)json.json["scenecamera"]["snapshots"];
            if (snapshotarr != null)
            {
                foreach (var s in snapshotarr)
                {
                    var time = s["time"].Value<double>();
                    var fileName = s["file"].ToString();
                    var x = await _recording.GetUri(fileName);
                    Dispatcher.Invoke(() =>
                    {
                        Snapshots.Add(new SnapshotVM(Dispatcher, x, TimeSpan.FromSeconds(time), fileName));
                    });
                }

                if (Snapshots.Any())
                    Thumbnail = Snapshots.First().Url.AbsoluteUri;
            }
        }

        public string FwVersion
        {
            get => _fwVersion;
            set
            {
                if (value == _fwVersion) return;
                _fwVersion = value;
                OnPropertyChanged();
            }
        }

        public string RuSerial
        {
            get => _ruSerial;
            set
            {
                if (value == _ruSerial) return;
                _ruSerial = value;
                OnPropertyChanged();
            }
        }

        public string HuSerial
        {
            get => _huSerial;
            set
            {
                if (value == _huSerial) return;
                _huSerial = value;
                OnPropertyChanged();
            }
        }

        public string Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (Equals(value, _thumbnail)) return;
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        private async Task PrepareReplay()
        {
            var res = await _recording.GazeDataAsync();
            _gazeQueue = res.Item1;
            _gazeLoadTimer = new Timer(100);
            _gazeLoadTimer.Elapsed += (sender, args) => FlushGazeQueue();
            _gazeLoadTimer.Enabled = true;

            res.Item2.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
            {
                _gazeLoadTimer.Enabled = false;
                FlushGazeQueue();
                GazeLoadedUntil = DurationInSeconds;
            });
        }

        private void FlushGazeQueue()
        {
            while (_gazeQueue.TryDequeue(out var g))
            {
                _gaze.Add(g);
            }
            if (_gaze.Any())
                GazeLoadedUntil = _gaze.Last().TimeStamp.TotalSeconds;
        }

        public DateTime Created
        {
            get => _created;
            set
            {
                if (value.Equals(_created)) return;
                _created = value;
                OnPropertyChanged();
            }
        }

        public string VisibleName
        {
            get => _visibleName;
            set
            {
                if (value == _visibleName) return;
                _visibleName = value;
                OnPropertyChanged();
            }
        }

        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                if (value.Equals(_duration)) return;
                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationInSeconds));
            }
        }

        public Guid Id => _recording.UUID;

        public Uri VideoUri { get; private set; }

        public double DurationInSeconds => Duration.TotalSeconds;

        public RelayCommand TogglePlay { get; }
        public RelayCommand DeleteRecording { get; }

        public TimeSpan Position
        {
            get => _position;
            set
            {
                if (value.Equals(_position)) return;
                _position = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionInSeconds));
                _media.Position = value;
                _ = SendRtaInfo();
            }
        }

        public double PositionInSeconds
        {
            get => Position.TotalSeconds;
            set => Position = TimeSpan.FromSeconds(value);
        }

        public double GazeLoadedUntil
        {
            get => _gazeLoadedUntil;
            set
            {
                if (value.Equals(_gazeLoadedUntil)) return;
                _gazeLoadedUntil = value;
                OnPropertyChanged();
            }
        }

        public double GazeMarkerSize
        {
            get => _gazeMarkerSize;
            set
            {
                if (value.Equals(_gazeMarkerSize)) return;
                _gazeMarkerSize = value;
                OnPropertyChanged();
            }
        }

        public double GazeX
        {
            get => _gazeX;
            set
            {
                if (value.Equals(_gazeX)) return;
                _gazeX = value;
                OnPropertyChanged();
            }
        }

        public double GazeY
        {
            get => _gazeY;
            set
            {
                if (value.Equals(_gazeY)) return;
                _gazeY = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand Download { get; }
        public RelayCommand StartRTA { get; }
        public RelayCommand StopRTA { get; }

        public ObservableCollection<SnapshotVM> Snapshots { get; } = new ObservableCollection<SnapshotVM>();

        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                _downloadProgress = value;
                OnPropertyChanged();
            }
        }

        public void Report(double value)
        {
            DownloadProgress = value;
        }
    }

    public class SnapshotVM : ObservableObject
    {
        public TimeSpan Time { get; }

        public Uri Url { get; }

        public string FileName { get; }

        public SnapshotVM(Dispatcher dispatcher, Uri url, TimeSpan time, string fileName) : base(dispatcher)
        {
            Time = time;
            Url = url;
            FileName = fileName;
        }
    }

    internal class RtaInfo
    {
        public TimeSpan Position { get; }
        public bool IsPlaying { get; }

        public RtaInfo(TimeSpan position, bool isPlaying)
        {
            Position = position;
            IsPlaying = isPlaying;
        }
    }

    internal class RtaMetaInfo
    {
        public Guid Rec { get; }
        public Guid RtaRec { get; }

        public RtaMetaInfo(Guid rec, Guid rtaRec)
        {
            Rec = rec;
            RtaRec = rtaRec;
        }
    }
}
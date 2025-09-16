using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Transactions;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const string configFilePath = "Config.json";

if (!File.Exists(configFilePath))
{
    var dummyConfig = new SoundConfigProfile([
        new("Example1.mid", []),
        new("Example2.mid", [1, 3])
    ]);
    var dummyJson = JsonSerializer.Serialize(dummyConfig, SerializerContext.Default.SoundConfigProfile);
    File.WriteAllText(configFilePath, dummyJson);
    var schema = SerializerContext.Default.SoundConfigProfile.GetJsonSchemaAsNode().ToJsonString();
    File.WriteAllText("Config.schema.json", schema);
    Console.WriteLine($"Config file not found. Dummy config file & schema file have been created at {Path.GetFullPath(configFilePath)}");
    return;
}

var configJson = File.ReadAllText(configFilePath);
var config = JsonSerializer.Deserialize(configJson, SerializerContext.Default.SoundConfigProfile);

if (config.Configs.Length == 0)
    return;

var cache = new Dictionary<int, AudioFileReaderAllocation>();

foreach (var (file, importantBars) in config.Configs)
{
    var clicks = CreateClickInfo(file);
    CreateAudioTrack(
        CollectionsMarshal.AsSpan(clicks),
        importantBars.AsSpan(),
        Path.ChangeExtension(file, "click.wav"),
        cache
    );
}

foreach (var allocation in cache.Values) allocation.Dispose();
cache.Clear();

return;

const int PrepareTimes = 2;

static List<ClickInfo> CreateClickInfo(string midiFilePath)
{
    var sourceMidiFile = MidiFile.Read(midiFilePath);
    var sourceTempoMap = sourceMidiFile.GetTempoMap();

    var maxMidiClicks = sourceMidiFile.Chunks
        .OfType<TrackChunk>()
        .SelectMany(trackChunk => trackChunk.GetNotes())
        .Max(note => note.Time);

    var timeSigChange = sourceTempoMap
        .GetTimeSignatureChanges()
        .Select(x => new TimeSignatureEvent(x.Time, x.Value))
        .ToList();

    if (timeSigChange.Count == 0 || timeSigChange[0].MidiTime != 0)
        timeSigChange.Insert(0, new(0, TimeSignature.Default));
    
    var timeQueue = new Queue<TimeSignatureEvent>(timeSigChange);
    var clickInfos = new List<ClickInfo>();
    var currentTimeSignature = timeQueue.Dequeue().TimeSignature;
    var oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);

    var oneBeatMetricLength = LengthConverter.ConvertTo<MetricTimeSpan>(oneBeatMusicalLength, 0, sourceTempoMap);
    
    for (var i = 0; i < currentTimeSignature.Numerator * PrepareTimes; i++)
    {
        clickInfos.Add(new((ulong)(i * oneBeatMetricLength.TotalMicroseconds),
            i % currentTimeSignature.Numerator == 0 ? ClickType.PreparePrimary : ClickType.PrepareSecondary));
    }

    var offsetMicroseconds = oneBeatMetricLength.TotalMicroseconds * currentTimeSignature.Numerator * PrepareTimes;
    long currentMidiTime = 0;
    var remainingBeats = currentTimeSignature.Numerator;
    while (currentMidiTime < maxMidiClicks)
    {
        clickInfos.Add(new((ulong)(TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds),
            remainingBeats == currentTimeSignature.Numerator
                ? ClickType.Primary
                : ClickType.Secondary));

        remainingBeats--;

        if (remainingBeats <= 0) remainingBeats = currentTimeSignature.Numerator;

        currentMidiTime += LengthConverter.ConvertFrom(
            oneBeatMusicalLength,
            currentMidiTime,
            sourceTempoMap
        );
        
        if (timeQueue.TryPeek(out var newTime) && newTime.MidiTime <= currentMidiTime)
        {
            currentTimeSignature = timeQueue.Dequeue().TimeSignature;
            oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);
            remainingBeats = currentTimeSignature.Numerator;
        }
    }
    
    var lastClickMicrosecond = TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds;
    clickInfos.Add(new((ulong)lastClickMicrosecond, ClickType.Final));

    return clickInfos;
}

static MusicalTimeSpan GetOneBeatMusicalLength(TimeSignature timeSignature)
{
    return new(1, timeSignature.Denominator);
}

static void CreateAudioTrack(ReadOnlySpan<ClickInfo> clickInfo, ReadOnlySpan<int> importantBarNumber, string dstPath,
    Dictionary<int, AudioFileReaderAllocation> cache)
{
    var sequencer = new WaveSequencer();
    const uint VoiceOffset = 120_000;
    var barNumber = 0;
    var prepareVoiceNumber = 1;
    var remainingCountDownBars = PrepareTimes;
    var importantSet = new HashSet<int>();
    foreach (var bar in importantBarNumber)
    {
        importantSet.Add(bar);
        importantSet.Add(bar - 1);
        importantSet.Add(bar - 2);
    }

    foreach (var click in clickInfo)
    {
        const string primaryClickPath = "ClickPrimary.wav";
        const string secondaryClickPath = "ClickSecondary.wav";
        AudioFileReader clickSample;
        AudioFileReader? voiceSample = null;
        switch (click.Type)
        {
            case ClickType.Primary:
                barNumber++;
                clickSample = new(primaryClickPath);
                if (importantSet.Contains(barNumber)) voiceSample = GetNumberVoice(barNumber, cache);
                break;
            case ClickType.Secondary:
                clickSample = new(secondaryClickPath);
                break;
            case ClickType.PreparePrimary:
                remainingCountDownBars--;
                prepareVoiceNumber = 1;
                clickSample = new(primaryClickPath);
                if(remainingCountDownBars == 0) voiceSample = GetNumberVoice(1, cache);
                break;
            case ClickType.PrepareSecondary:
                prepareVoiceNumber++;
                clickSample = new(secondaryClickPath);
                if(remainingCountDownBars == 0) voiceSample = GetNumberVoice(prepareVoiceNumber, cache);
                break;
            case ClickType.Final:
                clickSample = new(primaryClickPath);
                break;
            default:
                throw new UnreachableException();
        }

        sequencer.AddSample(clickSample, (long)(click.Microsecond + VoiceOffset));
        if (voiceSample == null) continue;
        sequencer.AddSample(voiceSample, (long)click.Microsecond);
    }
    WaveFileWriter.CreateWaveFile16(dstPath, sequencer.Bake());
}

static AudioFileReader GetNumberVoice(int number, Dictionary<int, AudioFileReaderAllocation> cachedAllocations)
{
    ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 9999);
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
    if (cachedAllocations.TryGetValue(number, out var allocation))
        return allocation.CreateReader();
    var path = Path.Combine("VoiceBank", $"{number:D4}.mp3");
    allocation = new(path);
    cachedAllocations[number] = allocation;
    return allocation.CreateReader();
}

public readonly struct AudioFileReaderAllocation : IDisposable
{
    private readonly string _tempFilePath;
    private readonly Stack<AudioFileReader> _activeReaders = new();
    
    public AudioFileReaderAllocation(string path)
    {
        var sourceReader = new AudioFileReader(path);
        var tmpDir = Path.GetTempPath();
        _tempFilePath = Path.Combine(tmpDir, $"{Guid.NewGuid()}.wav");
        WaveFileWriter.CreateWaveFile16(_tempFilePath, sourceReader);
        sourceReader.Dispose();
    }
    
    public AudioFileReader CreateReader()
    {
        var reader = new AudioFileReader(_tempFilePath);
        _activeReaders.Push(reader);
        return reader;
    }

    public void Dispose()
    {
        while (_activeReaders.TryPop(out var reader)) 
            reader.Dispose();
        if(!File.Exists(_tempFilePath)) return;
        File.Delete(_tempFilePath);
    }
}

public enum ClickType
{
    PreparePrimary,
    PrepareSecondary,
    Primary,
    Secondary,
    Final,
}

internal readonly record struct ClickInfo(
    ulong Microsecond,
    ClickType Type
);

internal record struct TimeSignatureEvent(long MidiTime, TimeSignature TimeSignature);

public record SoundConfig(string MidiFilePath, int[] ImportantBars);

public record struct SoundConfigProfile(SoundConfig[] Configs);

[JsonSerializable(typeof(SoundConfigProfile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class SerializerContext : JsonSerializerContext;

public class WaveSequencer
{
    private record struct Sample(AudioFileReader Provider, long MicrosecondPosition);
    private readonly List<Sample> _samples = [];
    
    public void AddSample(AudioFileReader sample, long microsecondPosition)
    {
        _samples.Add(new(sample, microsecondPosition));
    }

    public MixingSampleProvider Bake()
    {
        _samples.Sort((a, b) => Comparer<long>.Default.Compare(a.MicrosecondPosition, b.MicrosecondPosition));
        var tracks = new List<AudioTrack>();
        
        foreach (var sample in _samples)
        {
            var placed = false;
            foreach (var track in tracks)
            {
                if (!track.TryAppendSample(sample.Provider, sample.MicrosecondPosition)) continue;
                placed = true;
                break;
            }

            if (placed) continue;
            var newTrack = new AudioTrack();
            if (!newTrack.TryAppendSample(sample.Provider, sample.MicrosecondPosition))
            {
                throw new UnreachableException();
            }
            tracks.Add(newTrack);
        }

        return new(tracks
            .Select(track => track.CreateSampleProvider())
            .ToList());
    }

    private class AudioTrack
    {
        private long _headMicrosecondPosition;
        private readonly List<OffsetSampleProvider> _samples = [];

        public bool TryAppendSample(AudioFileReader provider, long requestedStartPosition)
        {
            var offset = requestedStartPosition - _headMicrosecondPosition;
            if (offset < 0) return false;
            var offsetProvider = new OffsetSampleProvider(provider)
            {
                DelayBy = TimeSpan.FromMicroseconds(offset)
            };
            _samples.Add(offsetProvider);
            _headMicrosecondPosition += offset;
            _headMicrosecondPosition += (long)provider.TotalTime.TotalMicroseconds;
            return true;
        }

        public ConcatenatingSampleProvider CreateSampleProvider() => new(_samples);
    }
}
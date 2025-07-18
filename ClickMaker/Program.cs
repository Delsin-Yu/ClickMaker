using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const string midiDir = @"D:\Desktop\ClickCreate";
const string primaryClickPath = "ClickPrimary.wav";
const string secondaryClickPath = "ClickSecondary.wav";

foreach (var file in Directory.GetFiles(
             midiDir,
             "*.mid",
             SearchOption.TopDirectoryOnly
         ))
{
    var clicks = CreateClickInfo(file);
    CreateAudioTrack(
        CollectionsMarshal.AsSpan(clicks),
        Path.ChangeExtension(file, "click.wav"),
        primaryClickPath,
        secondaryClickPath
     );
}

return;

static List<ClickInfo> CreateClickInfo(string midiFilePath)
{
    var sourceMidiFile = MidiFile.Read(midiFilePath);
    var sourceTempoMap = sourceMidiFile.GetTempoMap();

    var maxMicroseconds = sourceMidiFile.Chunks
        .OfType<TrackChunk>()
        .SelectMany(trackChunk => trackChunk.GetNotes())
        .Max(note => note.TimeAs<MetricTimeSpan>(sourceTempoMap).TotalMicroseconds);

    var timeSigChange = sourceTempoMap
        .GetTimeSignatureChanges()
        .Select(x => new TimeSignatureEvent(x.TimeAs<MetricTimeSpan>(sourceTempoMap).TotalMicroseconds, x.Value))
        .ToList();

    var tempoChange = sourceTempoMap
        .GetTempoChanges()
        .Select(x => new TempoEvent(x.TimeAs<MetricTimeSpan>(sourceTempoMap).TotalMicroseconds, x.Value))
        .ToArray();

    if (timeSigChange.Count == 0 || timeSigChange[0].Microsecond != 0)
        timeSigChange.Insert(0, new(0, TimeSignature.Default));
    
    var timeQueue = new Queue<TimeSignatureEvent>(timeSigChange);
    var tempoQueue = new Queue<TempoEvent>(tempoChange);
    var clickInfos = new List<ClickInfo>();

    var currentMicrosecond = 0L;

    var firstTempo = tempoQueue.Dequeue().Tempo;
    var firstTimeSignature = timeQueue.Dequeue().TimeSignature;

    var currentTempo = firstTempo;
    var currentTimeSignature = firstTimeSignature;

    var remainingBeats = currentTimeSignature.Numerator;

    for (var beat = 0; beat < firstTimeSignature.Numerator; beat++)
    {
        clickInfos.Add(new(currentMicrosecond, beat == 0 ? ClickType.Primary : ClickType.Secondary));
        currentMicrosecond += (long)Math.Round(firstTempo.MicrosecondsPerQuarterNote * (4d / firstTimeSignature.Denominator));
    }

    var offset = currentMicrosecond;
    currentMicrosecond = 0;
    var oneBeatTimeSpan = GetOneBeatMusicalLength(currentTimeSignature);
    var currentTimeSpan = new MetricTimeSpan(currentMicrosecond);
    
    while (currentMicrosecond < maxMicroseconds)
    {
        clickInfos.Add(new(currentMicrosecond + offset,
            remainingBeats == currentTimeSignature.Numerator
                ? ClickType.Primary
                : ClickType.Secondary));

        remainingBeats--;

        if (remainingBeats <= 0) remainingBeats = currentTimeSignature.Numerator;

        currentTimeSpan.UnsafeChangeTime(currentMicrosecond);
        var metricTimeLength = LengthConverter.ConvertTo<MetricTimeSpan>(
            oneBeatTimeSpan,
            currentTimeSpan,
            sourceTempoMap
        );
        
        currentMicrosecond += metricTimeLength.TotalMicroseconds;
        
        if (tempoQueue.TryPeek(out var newTempo) && newTempo.Microsecond <= currentMicrosecond) 
            currentTempo = tempoQueue.Dequeue().Tempo;

        if (timeQueue.TryPeek(out var newTime) && newTime.Microsecond <= currentMicrosecond)
        {
            currentTimeSignature = timeQueue.Dequeue().TimeSignature;
            oneBeatTimeSpan = GetOneBeatMusicalLength(currentTimeSignature);
            remainingBeats = currentTimeSignature.Numerator;
        }   
    }

    for (var beat = 0; beat < currentTimeSignature.Numerator; beat++)
    {
        clickInfos.Add(new(currentMicrosecond + offset, beat == 0 ? ClickType.Primary : ClickType.Secondary));
        currentMicrosecond += (long)Math.Round(currentTempo.MicrosecondsPerQuarterNote * (4d / currentTimeSignature.Denominator));
    }
    
    return clickInfos;
}

static MusicalTimeSpan GetOneBeatMusicalLength(TimeSignature timeSignature)
{
    return new(1, timeSignature.Denominator);
}

static void CreateAudioTrack(ReadOnlySpan<ClickInfo> clickInfo, string dstPath, string primaryClickPath, string secondaryClickPath)
{
    var sampleSequence = new List<ISampleProvider>();
    var currentMicrosecond = 0L;
    foreach (var click in clickInfo)
    {
        AudioFileReader clickSample = click.Type switch
        {
            ClickType.Primary => new(primaryClickPath),
            ClickType.Secondary => new(secondaryClickPath),
            _ => throw new UnreachableException()
        };

        if (sampleSequence.Count == 0)
        {
            sampleSequence.Add(clickSample);
        }
        else
        {
            var lastSample = sampleSequence[^1];
            TimeSpan lastSampleDuration;
            switch (lastSample)
            {
                case AudioFileReader fileSample:
                    lastSampleDuration = fileSample.TotalTime;
                    break;
                case OffsetSampleProvider offsetSample:
                    lastSampleDuration = ((AudioFileReader)offsetSample.UnsafeAccessInternalSampleProvider()).TotalTime;
                    break;
                default:
                    throw new UnreachableException();
            }
            
            var offsetSampleProvider = new OffsetSampleProvider(clickSample);
            var delayAmount = TimeSpan.FromMicroseconds(click.Microsecond - currentMicrosecond) - lastSampleDuration;
            offsetSampleProvider.DelayBy = delayAmount;
            sampleSequence.Add(offsetSampleProvider);
        }

        currentMicrosecond = click.Microsecond;
    }

    var concatenatingSampleProvider = new ConcatenatingSampleProvider(sampleSequence);
    // var output = new DirectSoundOut(1024);
    // output.Init(concatenatingSampleProvider);
    // output.Play();
    // while (output.PlaybackState == PlaybackState.Playing)
    // {
    //     Thread.Sleep(2000);
    // }

    WaveFileWriter.CreateWaveFile16(dstPath, concatenatingSampleProvider);
}

public enum ClickType
{
    Primary,
    Secondary,
}

internal readonly record struct ClickInfo(
    long Microsecond,
    ClickType Type
)
{
    public override string ToString()
    {
        return $"{(Type == ClickType.Primary ? 'P' : 'S')} {TimeSpan.FromMicroseconds(Microsecond).TotalSeconds}ms";
    }
}

internal record struct TimeSignatureEvent(long Microsecond, TimeSignature TimeSignature);

internal record struct TempoEvent(long Microsecond, Tempo Tempo);

public static class Accessor
{
    public static ISampleProvider UnsafeAccessInternalSampleProvider(this OffsetSampleProvider provider) =>
        AccessorImpl1(provider);
    
    public static void UnsafeChangeTime(this MetricTimeSpan metricTimeSpan, long totalMicroseconds) =>
        AccessorImpl2(metricTimeSpan) = new(totalMicroseconds * 10);
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "sourceProvider")]
    private static extern ref ISampleProvider AccessorImpl1(OffsetSampleProvider provider);
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_timeSpan")]
    private static extern ref TimeSpan AccessorImpl2(MetricTimeSpan time);
}
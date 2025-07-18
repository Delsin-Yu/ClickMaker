using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

const string midiDir = @"D:\Desktop\ClickCreate";

foreach (var file in Directory.GetFiles(
             midiDir,
             "*.mid",
             SearchOption.TopDirectoryOnly
         ))
{
    var sourceMidiFile = MidiFile.Read(file);
    var sourceTempoMap = sourceMidiFile.GetTempoMap();
    var midiBeatList = new List<ulong>(); // Milliseconds

    var maxTick = sourceMidiFile.Chunks
        .OfType<TrackChunk>()
        .SelectMany(trackChunk => trackChunk.GetNotes())
        .Max(note => note.Time);
    
    var timeSigChange = sourceTempoMap
        .GetTimeSignatureChanges()
        .Select(x => new TimeSignatureEvent(x.Time, x.Value))
        .ToList();
    var tempoChange = sourceTempoMap
        .GetTempoChanges()
        .Select(x => new TempoEvent(x.Time, x.Value))
        .ToList();
    if (timeSigChange.Count == 0 || timeSigChange[0].MidiTick != 0) 
        timeSigChange.Insert(0, new(0, TimeSignature.Default));
    var clicks = new List<ClickInfo>();
    var currentTick = 0L;
    var currentTimeSignature = timeSigChange[0].TimeSignature;
    while (currentTick < maxTick)
    {
        currentTick++;
    }
}


record struct TimeSignatureEvent(long MidiTick, TimeSignature TimeSignature);
record struct TempoEvent(long MidiTick, Tempo Tempo);

public enum ClickType
{
    Primary,
    Secondary,
}

record struct ClickInfo(long MidiTick, ClickType Type);
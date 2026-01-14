using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using MidiCS.Events;

namespace MidiCS
{
  public class MidiFileReader
  {
    private static string DecodeText(byte[] bytes)
    {
        // Try strict UTF-8 first (throws on invalid sequences).
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            return utf8.GetString(bytes);
        }
        catch (System.ArgumentException)
        {
            // Invalid UTF-8 -> fall back to Windows-1252 (typical single-byte MIDI text)
            try
            {
                return System.Text.Encoding.GetEncoding(1252).GetString(bytes);
            }
            catch
            {
                // Last resort: 28591 (Latin1) to preserve raw byte->codepoint mapping
                return System.Text.Encoding.GetEncoding(28591).GetString(bytes);
            }
        }
    }
        public static MidiFile FromBytes(byte[] bytes)
    {
      using (var s = new MemoryStream(bytes))
        return FromStream(s);
    }
    public static MidiFile FromStream(Stream stream)
    {
      // "MThd" big-endian, header size always = 6
      if (stream.ReadInt32BE() != 0x4D546864 || stream.ReadInt32BE() != 0x6)
        throw new InvalidDataException("MIDI file did not begin with proper MIDI header.");
      var format = (MidiFormat)stream.ReadUInt16BE();
      if (format > MidiFormat.MultiTrack)
        throw new NotSupportedException("MIDI format " + format + " is not supported by this library.");
      var tracks = new List<MidiTrack>(stream.ReadUInt16BE());
      var ticksPerQn = stream.ReadUInt16BE();
      if ((ticksPerQn & 0x8000) == 0x8000)
        throw new NotSupportedException("SMPTE delta time format is not supported by this library.");
      for (int i = 0; i < tracks.Capacity; i++)
      {
        tracks.Add(readTrack(stream));
      }
      return new MidiFile(format, tracks, ticksPerQn);
    }

    private static MidiTrack readTrack(Stream stream)
    {
      if (stream.ReadInt32BE() != 0x4D54726B)
        throw new InvalidDataException("MIDI track not recognized.");
      long trkLen = stream.ReadUInt32BE();
      List<IMidiMessage> messages = new List<IMidiMessage>();
      long totalTicks = 0;
      string name = "";
      while (trkLen > 0)
      {
        long pos = stream.Position;
        var newMsg = readMessage(stream);
        messages.Add(newMsg);
        if (newMsg is Events.TrackName)
          name = (newMsg as Events.TrackName).Text;
        totalTicks += newMsg.DeltaTime;
        trkLen -= stream.Position - pos; // subtract message length from total track length
      }
      return new MidiTrack(messages, totalTicks, name);
    }


    static byte lastStatus = 0;

    /// <summary>
    /// Reads a single Midi message from the given stream.
    /// If the message uses running status, the status from the last call to
    /// this method will be used.
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    public static IMidiMessage readMessage(Stream s)
    {
      uint deltaTime = s.ReadMidiMultiByte();
      byte status = s.ReadUInt8();
      if (status < 0x80) // running status
      {
        status = lastStatus;
        s.Position--;
      }
      else
      {
        if (status < 0xF0) // meta events do not trigger running status?
          lastStatus = status;
      }
      byte channel = (byte)(status & 0xF);
      byte key, velocity, pressure, controller, value;
      ushort pitchBend;
      EventType eventType = (EventType)(status & 0xF0);
      switch (eventType)
      {
        case EventType.NoteOff:
          key = s.ReadUInt8();
          velocity = s.ReadUInt8();
          return new NoteOffEvent(deltaTime, channel, key, velocity);
        case EventType.NoteOn:
          key = s.ReadUInt8();
          velocity = s.ReadUInt8();
          return new NoteOnEvent(deltaTime, channel, key, velocity);
        case EventType.NotePresure:
          key = s.ReadUInt8();
          pressure = s.ReadUInt8();
          return new NotePressureEvent(deltaTime, channel, key, pressure);
        case EventType.Controller:
          controller = s.ReadUInt8();
          value = s.ReadUInt8();
          return new ControllerEvent(deltaTime, channel, controller, value);
        case EventType.ProgramChange:
          value = s.ReadUInt8();
          return new ProgramChgEvent(deltaTime, channel, value);
        case EventType.ChannelPressure:
          pressure = s.ReadUInt8();
          return new ChannelPressureEvent(deltaTime, channel, pressure);
        case EventType.PitchBend:
          pitchBend = s.ReadUInt16LE();
          return new PitchBendEvent(deltaTime, channel, pitchBend);
      }
      if (status == 0xFF) // meta event
      {
        byte type = s.ReadUInt8();
        int length = (int)s.ReadMidiMultiByte();
        byte[] tmp;
        switch ((MetaEventType)type)
        {
          case MetaEventType.SequenceNumber:
            if (length != 2)
              throw new InvalidDataException("Sequence number events must have 2 bytes of data; this one has " + length);
            return new SequenceNumber(deltaTime, s.ReadUInt16BE());
          case MetaEventType.TextEvent:
            {
                var data = s.ReadBytes(length);
                return new TextEvent(deltaTime, DecodeText(data));
            }
          case MetaEventType.CopyrightNotice:
            {
                var data = s.ReadBytes(length);
                return new CopyrightNotice(deltaTime, DecodeText(data));
            }
          case MetaEventType.TrackName:
            {
                var data = s.ReadBytes(length);
                return new TrackName(deltaTime, DecodeText(data));
            }
          case MetaEventType.InstrumentName:
            {
                var data = s.ReadBytes(length);
                return new InstrumentName(deltaTime, DecodeText(data));
            }
          case MetaEventType.Lyric:
            {
                var data = s.ReadBytes(length);
                return new Lyric(deltaTime, DecodeText(data));
            }
          case MetaEventType.Marker:
            {
                var data = s.ReadBytes(length);
                return new Marker(deltaTime, DecodeText(data));
            }
          case MetaEventType.CuePoint:
            {
                var data = s.ReadBytes(length);
                return new CuePoint(deltaTime, DecodeText(data));
            }
          case MetaEventType.ChannelPrefix:
            if (length != 1)
              throw new InvalidDataException("Channel prefix events must have 1 byte of data; this one has " + length);
            return new ChannelPrefix(deltaTime, s.ReadUInt8());
          case MetaEventType.EndOfTrack:
            return new EndOfTrackEvent(deltaTime);
          case MetaEventType.TempoEvent:
            if (length != 3)
              throw new InvalidDataException("Tempo events must have 3 bytes of data; this one has " + length);
            return new TempoEvent(deltaTime, s.ReadUInt24BE());
          case MetaEventType.SmtpeOffset:
            if (length != 5)
              throw new InvalidDataException("SMTPE Offset events must have 5 bytes of data; this one has " + length);
            tmp = s.ReadBytes(length);
            return new SmtpeOffset(deltaTime, tmp[0], tmp[1], tmp[2], tmp[3], tmp[4]);
          case MetaEventType.TimeSignature:
            if (length != 4)
              throw new InvalidDataException("Time Signature events must have 4 bytes of data; this one has " + length);
            tmp = s.ReadBytes(length);
            return new TimeSignature(deltaTime, tmp[0], tmp[1], tmp[2], tmp[3]);
          case MetaEventType.KeySignature:
            if (length != 2)
              throw new InvalidDataException("Key Signature events must have 2 bytes of data; this one has " + length);
            tmp = s.ReadBytes(length);
            return new KeySignature(deltaTime, tmp[0], tmp[1]);
          case MetaEventType.SequencerSpecific:
            return new SequencerSpecificEvent(deltaTime, s.ReadBytes(length));
          default: // unknown meta event, just skip past it.
            s.Position += length;
            return null;
        }
      }
      else // sysex
      {
        if (status == 0xF0) // should prefix Sysex with F0 (start-of-exclusive)
        {
          byte[] data = new byte[s.ReadMidiMultiByte() + 1];
          data[0] = 0xF0;
          s.Read(data, 1, data.Length - 1);
          return new SysexEvent(deltaTime, data);
        }
        else
        {
          return new SysexEvent(deltaTime, s.ReadBytes((int)s.ReadMidiMultiByte()));
        }
      }
    }
  }
}

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.HID;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// PlayStation 5 DualSense (and Edge): rumble motors and adaptive triggers written together
    /// in one raw HID output report (DualSenseReport), over USB or Bluetooth.
    ///
    /// Unity's own DualSenseGamepadHID.SetMotorSpeeds is deliberately not used: it only sends the
    /// USB report (its Bluetooth path is an acknowledged FIXME), blacks out the lightbar on every
    /// call, and has no trigger support. This writer never touches the lightbar flags.
    ///
    /// The pad accepts one output command at a time, so state is merged and sent at most once a
    /// frame, and only when it changed; a refused send stays dirty and retries next frame.
    /// </summary>
    public sealed class DualSenseHaptics : IPadHaptics
    {
        // Big enough for the largest output report a DualSense HID descriptor declares (the
        // Bluetooth interface lists audio reports of a few hundred bytes); the command's own size
        // field says how much of it the backend writes
        private const int MaxPayload = 640;

        [StructLayout(LayoutKind.Explicit, Size = InputDeviceCommand.BaseCommandSize + MaxPayload)]
        private struct HidOutputCommand : IInputDeviceCommandInfo
        {
            public static FourCC Type => new FourCC('H', 'I', 'D', 'O');
            public FourCC typeStatic => Type;

            [FieldOffset(0)] public InputDeviceCommand baseCommand;
        }

        private readonly DualSenseGamepadHID pad;
        private readonly bool bluetooth;
        private readonly int commandPayloadSize;
        private readonly byte[] report = new byte[DualSenseReport.BluetoothLength];
        private byte sequence;

        // What the pad holds now (last accepted send) vs. what this frame wants
        private float sentLow = -1f, sentHigh = -1f;
        private TriggerEffect sentLeft, sentRight;
        private bool triggersKnown;

        public DualSenseHaptics(DualSenseGamepadHID pad)
        {
            this.pad = pad;

            // USB declares a 48-byte output report (Edge up to 64); the Bluetooth interface
            // declares far larger ones. The backend writes the declared size, as Unity's own
            // DualSense command does
            int declared = 0;
            try
            {
                declared = HID.HIDDeviceDescriptor.FromJson(pad.description.capabilities).outputReportSize;
            }
            catch (Exception)
            {
                // Unparseable capabilities: assume USB, the only path Unity itself relies on
            }
            bluetooth = declared > 64;
            int minimum = bluetooth ? DualSenseReport.BluetoothLength : DualSenseReport.UsbLength;
            commandPayloadSize = Mathf.Clamp(declared, minimum, MaxPayload);
        }

        public UnityEngine.InputSystem.Gamepad Pad => pad;
        public bool NativeTriggers => true;
        public MotorCurve Curve { private get; set; } = MotorCurve.Identity;

        public string Route => bluetooth ? "DualSense Bluetooth" : "DualSense USB";
        public float SentLow => sentLow;
        public float SentHigh => sentHigh;
        public bool LastWriteOk { get; private set; } = true;

        public bool Covers(UnityEngine.InputSystem.Gamepad other) => other == pad;

        public void Apply(float low, float high, in TriggerEffect left, in TriggerEffect right, float deltaTime)
        {
            if (!pad.added) return;

            low = Curve.Apply(low);
            high = Curve.Apply(high);

            byte flags = 0;
            if (!Mathf.Approximately(low, sentLow) || !Mathf.Approximately(high, sentHigh))
                flags |= DualSenseReport.FlagRumble;
            if (!triggersKnown || !left.Equals(sentLeft)) flags |= DualSenseReport.FlagLeftTrigger;
            if (!triggersKnown || !right.Equals(sentRight)) flags |= DualSenseReport.FlagRightTrigger;
            if (flags == 0) return;

            if (!Send(flags, low, high, left, right)) return;   // busy: everything stays dirty

            sentLow = low;
            sentHigh = high;
            sentLeft = left;
            sentRight = right;
            triggersKnown = true;
        }

        public bool Silence()
        {
            if (!pad.added) return true;   // gone: nothing left to silence
            byte flags = DualSenseReport.FlagRumble | DualSenseReport.FlagLeftTrigger | DualSenseReport.FlagRightTrigger;
            if (Send(flags, 0f, 0f, TriggerEffect.Off, TriggerEffect.Off))
            {
                sentLow = sentHigh = 0f;
                sentLeft = sentRight = TriggerEffect.Off;
                triggersKnown = true;
                return true;
            }

            // Busy: forget what the pad holds so the next Apply or Silence resends every block
            sentLow = sentHigh = -1f;
            triggersKnown = false;
            return false;
        }

        private bool Send(byte flags, float low, float high, in TriggerEffect left, in TriggerEffect right)
        {
            int length = DualSenseReport.Build(report, bluetooth, sequence, flags, low, high, left, right);
            if (bluetooth) sequence = (byte)((sequence + 1) & 0x0F);

            var command = new HidOutputCommand
            {
                baseCommand = new InputDeviceCommand(HidOutputCommand.Type,
                    InputDeviceCommand.BaseCommandSize + commandPayloadSize)
            };

            // Safe-code byte view of the command (the project builds without unsafe)
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref command, 1));
            report.AsSpan(0, length).CopyTo(bytes.Slice(InputDeviceCommand.BaseCommandSize));

            LastWriteOk = pad.ExecuteCommand(ref command) >= 0;
            return LastWriteOk;
        }
    }
}

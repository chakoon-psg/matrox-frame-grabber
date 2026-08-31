using System.Collections.Generic;
using System.Text;
using Matrox.MatroxImagingLibrary;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Thin, self-contained wrapper over MIL's GenICam SFNC feature access for one digitizer.
    /// Every call is guarded (presence check + try/catch(MILException)) so cameras that lack a
    /// given feature simply fail softly. Set <see cref="Digitizer"/> after each (re)allocation.
    /// </summary>
    public sealed class GenICamFeatures
    {
        /// <summary>The digitizer these features operate on (M_NULL when no camera).</summary>
        public MIL_ID Digitizer { get; set; } = MIL.M_NULL;

        private bool HasDigitizer => Digitizer != MIL.M_NULL;

        /// <summary>True if the camera exposes the named feature.</summary>
        public bool Available(string name)
        {
            if (!HasDigitizer) return false;
            try
            {
                bool present = false;
                MIL.MdigInquireFeature(Digitizer, MIL.M_FEATURE_PRESENT, name, MIL.M_TYPE_BOOLEAN, ref present);
                return present;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Writes a string/enum feature (e.g. TriggerMode="On"). Returns false if unavailable.</summary>
        public bool SetString(string name, string value)
        {
            if (!Available(name)) return false;
            try
            {
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_STRING, value);
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Writes a double feature (e.g. ExposureTime). Returns false if unavailable.</summary>
        public bool SetDouble(string name, double value)
        {
            if (!Available(name)) return false;
            try
            {
                double v = value;
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_DOUBLE, ref v);
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>
        /// Writes an integer feature (e.g. Width, OffsetX). Returns false if unavailable.
        ///
        /// Integer features MUST go through M_TYPE_MIL_INT. Writing one with M_TYPE_DOUBLE is
        /// silently ignored — no exception, no error, the value simply does not change.
        /// </summary>
        public bool SetInt(string name, long value)
        {
            if (!Available(name)) return false;
            try
            {
                MIL_INT v = value;
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_MIL_INT, ref v);
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>
        /// Reads an integer feature property (M_FEATURE_VALUE / _MIN / _MAX / _INCREMENT).
        /// </summary>
        public bool TryGetInt(long inquireType, string name, out long value)
        {
            value = 0;
            if (!HasDigitizer) return false;
            try
            {
                MIL_INT v = 0;
                MIL.MdigInquireFeature(Digitizer, inquireType, name, MIL.M_TYPE_MIL_INT, ref v);
                value = v;
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Reads a double feature property (M_FEATURE_VALUE / _MIN / _MAX / ...).</summary>
        public bool TryGetDouble(long inquireType, string name, out double value)
        {
            value = 0;
            if (!HasDigitizer) return false;
            try
            {
                double v = 0;
                MIL.MdigInquireFeature(Digitizer, inquireType, name, MIL.M_TYPE_DOUBLE, ref v);
                value = v;
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Reads a string/enum feature's current value.</summary>
        public bool TryGetString(string name, out string value)
        {
            value = "";
            if (!HasDigitizer) return false;
            try
            {
                var sb = new StringBuilder(256);
                MIL.MdigInquireFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_STRING, sb);
                value = sb.ToString();
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Enumerates the entry names of an enumeration feature (e.g. TriggerSource).</summary>
        public List<string> EnumEntries(string feature)
        {
            var list = new List<string>();
            if (!Available(feature)) return list;
            try
            {
                MIL_INT count = 0;
                MIL.MdigInquireFeature(Digitizer, MIL.M_FEATURE_ENUM_ENTRY_COUNT, feature, MIL.M_TYPE_MIL_INT, ref count);
                long n = count;
                for (long i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(256);
                    MIL.MdigInquireFeature(Digitizer, MIL.M_FEATURE_ENUM_ENTRY_NAME + i, feature, MIL.M_TYPE_STRING, sb);
                    string name = sb.ToString();
                    if (!string.IsNullOrEmpty(name))
                        list.Add(name);
                }
            }
            catch (MILException) { }
            return list;
        }

        /// <summary>Writes a boolean feature (e.g. AcquisitionFrameRateEnable).</summary>
        public bool SetBool(string name, bool value)
        {
            if (!Available(name)) return false;
            try
            {
                bool v = value;
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_BOOLEAN, ref v);
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Reads a boolean feature's current value.</summary>
        public bool TryGetBool(string name, out bool value)
        {
            value = false;
            if (!HasDigitizer) return false;
            try
            {
                bool v = false;
                MIL.MdigInquireFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_BOOLEAN, ref v);
                value = v;
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>Executes a command feature (e.g. TriggerSoftware).</summary>
        public bool ExecuteCommand(string name)
        {
            if (!Available(name)) return false;
            try
            {
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_COMMAND);
                return true;
            }
            catch (MILException) { return false; }
        }
    }
}

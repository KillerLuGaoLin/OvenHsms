namespace Hsms.Public
{
    public sealed class OvenParameters
    {
        public string Ppid { get; set; }

        public string Ip { get; set; }

        public int CurrentSegment { get; set; }

        public int TotalSteps { get; set; }

        public int RemainTimeHour { get; set; }

        public int RemainTimeMin { get; set; }

        public int CurrentTemp { get; set; }

        public string OperatingStatus { get; set; }

        public int CurrentTempSetting { get; set; }

        public OvenParameters()
        {
            Ppid = string.Empty;
            Ip = string.Empty;
            OperatingStatus = string.Empty;
        }
    }
}

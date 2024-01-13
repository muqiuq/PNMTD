using PNMTD.Helper;

namespace PNMTD
{
    public static class GlobalConfiguration
    {
        private static readonly ILogger _logger = LogManager.CreateLogger<Program>();

        public static int MAXIMUM_NUM_OF_EVENTS_PER_SENSOR = 2000;

        public static int MAXIMUM_NUM_OF_MAILLOG_ENTRIES = 50000;

        public static int MAXIMUM_NUM_OF_STORED_MAILS_IN_MAILLOG = 10000;

        public static int MAXIMUM_NUM_OF_YOUNGER_SIBLINGS_SENSORS = 64;

        public static int MINIMUM_TIME_DIFFERENCE_BETWEEN_EVENTS_IN_SECONDS = 5;

        public static string LinuxBasePath = "/var/lib/pnmtd/";

    }
}

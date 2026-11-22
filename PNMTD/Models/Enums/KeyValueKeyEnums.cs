namespace PNMTD.Models.Enums
{
    public enum KeyValueKeyEnums
    {
        LAST_HEARTBEAT_TASK_RUN = 10,
        NUM_OF_HEARTBEAT_SENSORS_PROCESSED = 11,
        LAST_NOTIFICATION_TASK_RUN = 50,
        LAST_PING_TASK_RUN = 100,
        NUM_OF_SUCCESSFULL_PINGS = 101,
        NUM_OF_FAILED_PINGS = 102,
        LAST_MAILBOX_CHECK = 150,
        LAST_MAIL_PROCESSING = 200,
        LAST_TIMESPAN_CONDITIONS_CHECK_TASK_RUN = 201,

        LAST_UPLINK_CHECK = 210,
        LAST_UPLINK_CHECKED_LINK = 211,
        LAST_UPLINK_CHECK_SUCCESSFULL = 212,
        UPLINK_OK = 213,
        // The uplink's state across restarts (docs/ai-operations-plan.md §7.2).
        UPLINK_FAILURES_IN_A_ROW = 214,
        UPLINK_DOWN_SINCE = 215,

        MAILBOX_DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS = 230,
        MAILBOX_DEFAULT_MARGIN_IN_DAYS = 231,

        LAST_DNS_CHECK = 240,
        DNS_SERVERS = 241,

        LAST_MIKROTIK_POLL_RUN = 250,
        LAST_MIKROTIK_VERSION_CHECK = 251,

        // The audit log's daily retention (docs/audit-redmine-zabbix-plan.md §3.8).
        LAST_AUDIT_RETENTION = 260,

        // Redmine's copy of linked tickets (docs/audit-redmine-zabbix-plan.md §6.7).
        LAST_REDMINE_SYNC = 270,

        // Zabbix's problems and hosts (docs/audit-redmine-zabbix-plan.md §7.3).
        LAST_ZABBIX_POLL = 280,
        LAST_ZABBIX_HOST_SYNC = 281,

        // Cookie's case book (docs/cookie-plan.md §19.3).
        LAST_COOKIE_CASEBOOK = 290,

        // The MCP tokens' expiry (docs/mcp-plan.md §4.3).
        LAST_MCP_TOKEN_EXPIRY = 300,

        // The SSL certificate sensors' checks and evaluations (docs/ssl-certificate-plan.md §5.2).
        LAST_CERTIFICATE_CHECK = 310,

        // The advanced sensors' evaluator (docs/advanced-sensor-plan.md §7.3).
        LAST_ADVANCED_EVALUATION = 320,

        // The 3rd Party Connectors' worker (docs/connectors-plan.md §9.1).
        LAST_CONNECTOR_WORK = 330,

        // The network map's schedule and retention (docs/network-map-plan.md §7.3).
        LAST_NETWORK_SCAN_SCHEDULE_RUN = 340,

        // The notification task's second timer: notifications to people (docs/knowledge-base-extensions-plan.md §10.5).
        LAST_USER_NOTIFICATION_RUN = 350,

        UNUSED = 999,
    }
}

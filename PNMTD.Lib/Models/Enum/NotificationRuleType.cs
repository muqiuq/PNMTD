using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Enum
{
    public enum NotificationRuleType
    {
        ALWAYS,
        ONLY_ON_CHANGE,
        ONLY_FAILURES,
        ONLY_FAILURES_ON_CHANGE
    }
}

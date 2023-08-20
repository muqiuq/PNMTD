using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Logic.IntervalDescriptions
{
    public class IntervalDescription
    {

        public DayOfWeek DayOfWeek { get; private set; } = DayOfWeek.Monday;

        public IntervalDescriptionType Type { get; private set; }
        public int HourOfDay { get; private set; } = 0;

        public int DayOfMonth { get; private set; } = 1;

        public IntervalDescription(IntervalDescriptionType type)
        {
            this.Type = type;
        }

        public IntervalDescription(IntervalDescriptionType type, DayOfWeek dayOfWeek)
        {
            this.Type = type;
            this.DayOfWeek = dayOfWeek;
        }

        public IntervalDescription(IntervalDescriptionType type, int hourOfDayOrDayOfMonth)
        {
            this.Type = type;
            if(this.Type == IntervalDescriptionType.MONTHLY)
            {
                DayOfMonth = hourOfDayOrDayOfMonth;
            }
            else
            {
                this.HourOfDay = hourOfDayOrDayOfMonth;
            }
        }

        public bool IsNow()
        {
            return IsNow(DateTime.Now);
        }

        public bool IsNow(DateTime now)
        {
            if(Type == IntervalDescriptionType.HOURLY)
            {
                if (now.Minute == 59 || now.Minute == 0 || now.Minute == 1) return true;
            }
            if(Type == IntervalDescriptionType.WEEKLY)
            {
                if(now.DayOfWeek == DayOfWeek && now.Hour == 0 && (now.Minute == 59 || now.Minute == 0 || now.Minute == 1)) return true;
            }
            if(Type == IntervalDescriptionType.MONTHLY)
            {
                if (now.Day == DayOfMonth && now.Hour == 0 && (now.Minute == 59 || now.Minute == 0 || now.Minute == 1)) return true;
            }
            if (Type == IntervalDescriptionType.DAILY)
            {
                if (now.Hour == HourOfDay && (now.Minute == 59 || now.Minute == 0 || now.Minute == 1)) return true;
            }
            return false;
        }

        public bool TimespanDifBiggerThenInterval(DateTime now, DateTime other)
        {
            var diff = now - other;
            if (Type == IntervalDescriptionType.HOURLY && diff > TimeSpan.FromHours(1))
            {
                return true;
            }
            if (Type == IntervalDescriptionType.WEEKLY && diff > TimeSpan.FromDays(7))
            {
                return true;
            }
            if (Type == IntervalDescriptionType.MONTHLY && diff > TimeSpan.FromHours(730))
            {
                return true;
            }
            if (Type == IntervalDescriptionType.DAILY && diff > TimeSpan.FromHours(24))
            {
                return true;
            }
            return false;
        }
    }
}

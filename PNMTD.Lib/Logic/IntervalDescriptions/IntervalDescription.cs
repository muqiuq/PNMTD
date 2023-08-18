using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Logic.IntervalDescriptions
{
    public class IntervalDescription
    {

        public DayOfWeek DayOfWeek { get; private set; }

        public IntervalDescriptionType Type { get; private set; }
        public int HourOfDay { get; private set; }

        public int DayOfMonth { get; private set; }

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

    }
}

using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PNMTD.Lib.Logic.IntervalDescriptions
{
    public class IntervalDescriptionHelper
    {
        public static bool TryParse(string value, out IntervalDescription description)
        {
            if (value == null)
            {
                description = null;
                return false;
            }
            try
            {
                description = Parse(value);
                return true;
            }catch(IntervalDescriptionParseException ex)
            {
                description = null;
                return false;
            }
        }

        public static IntervalDescription Parse(string v)
        {
            v = v.Replace("  ", " ");
            var parts = v.Split(" ");
            if(parts.Any(x => x == "week") || parts.Any(x => x == "weekly"))
            {
                return new IntervalDescription(IntervalDescriptionType.WEEKLY, ExtractDayOfWeek(v));
            }
            if (parts.Any(x => x == "monthly") || parts.Any(x => x == "month"))
            {
                Regex regex = new Regex(@"\d+");

                var match = regex.Match(v);

                if(!match.Success || match.Groups.Count != 1)
                {
                    throw new IntervalDescriptionParseException("For monthly number of day in month is required");
                }
                var enteredDayOfTheMonth = int.Parse(match.Value);
                if(enteredDayOfTheMonth < 1 || enteredDayOfTheMonth > 28)
                {
                    throw new IntervalDescriptionParseException("Invalid day of the month. Must be between 0 and 28");
                }
                return new IntervalDescription(IntervalDescriptionType.MONTHLY, int.Parse(match.Value));
            }
            if (parts.Any(x => x == "daily") || parts.Any(x => x == "day"))
            {
                Regex regex = new Regex(@"\d+");

                var match = regex.Match(v);

                if (match.Success)
                {
                    if(match.Groups.Count != 1)
                    {
                        throw new IntervalDescriptionParseException("Only one single number expected for hour of the day");
                    }
                    var enteredHourOfTheDay = int.Parse(match.Value);
                    if (enteredHourOfTheDay < 0 || enteredHourOfTheDay > 23)
                    {
                        throw new IntervalDescriptionParseException("Invalid hour of the day. Must be between 0 and 23");
                    }
                    return new IntervalDescription(IntervalDescriptionType.DAILY, enteredHourOfTheDay);
                }
                return new IntervalDescription(IntervalDescriptionType.DAILY);
            }
            throw new IntervalDescriptionParseException("Could not parse");
        }

        public static DayOfWeek ExtractDayOfWeek(string dow)
        {
            var weekNames = Enum.GetNames<DayOfWeek>();
            var instr = dow.ToLower();
            string selectedDayOfWeek = null;
            int matches = 0;
            foreach(var day in weekNames)
            {
                if(instr.Contains(day.ToLower()))
                {
                    matches++;
                    selectedDayOfWeek = day;
                }
            }
            if (matches != 1 || selectedDayOfWeek.IsNullOrEmpty()) throw new IntervalDescriptionParseException("failure extracing dayofweek");
            return Enum.Parse<DayOfWeek>(selectedDayOfWeek);
        }
    }
}

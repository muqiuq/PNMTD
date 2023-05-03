using Microsoft.EntityFrameworkCore;
using PNMTD.Models.Db;

namespace PNMTD.Data
{
    public static class DbSetEventEntityExtensions
    {

        public static void CleanUpEntitiesForHost(this DbSet<EventEntity> eventEntityDbSet, Guid sensorId)
        {
            var listOfEventsToDelete = eventEntityDbSet.Where(e => e.Sensor.Id == sensorId).OrderByDescending(e => e.Created)
                .Skip(GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR).ToList();

            eventEntityDbSet.RemoveRange(listOfEventsToDelete);
        }

    }
}

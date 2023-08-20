using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class MailLogExtensions
    {

        public static MailLogPoco ToPoco(this MailLogEntity mailLogEntity)
        {
            return new MailLogPoco
            {
                Id = mailLogEntity.Id,
                From = mailLogEntity.From,
                To = mailLogEntity.To,
                Content = mailLogEntity.Content,
                Created = mailLogEntity.Created,
                Processed = mailLogEntity.Processed,
                ProcessedById = mailLogEntity.ProcessedById,
                ProcessLog = mailLogEntity.ProcessLog,
                Subject = mailLogEntity.Subject,
                MessageDate = mailLogEntity.MessageDate,
            };
        }

        public static MailLogEntity ToEntity(this MailLogPoco mailLogPoco, bool isNew)
        {
            var mailLogEntity = new MailLogEntity()
            {
                Id = mailLogPoco.Id,
                From = mailLogPoco.From,
                To = mailLogPoco.To,
                Content = mailLogPoco.Content,
                Created = mailLogPoco.Created,
                Processed = mailLogPoco.Processed,
                ProcessedById = mailLogPoco.ProcessedById,
                ProcessLog = mailLogPoco.ProcessLog,
                Subject = mailLogPoco.Subject,
                MessageDate = mailLogPoco.MessageDate,
            };

            if (!isNew)
            {
                mailLogEntity.Id = mailLogPoco.Id;
            }
            else
            {
                mailLogEntity.Id = Guid.NewGuid();
            }

            return mailLogEntity;
        }
    }
}

using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class MailInputPocoExtensions
    {

        public static MailInputPoco ToPoco(this MailInputEntity mailInputEntity)
        {
            return new MailInputPoco
            {
                Id = mailInputEntity.Id,
                Enabled = mailInputEntity.Enabled,
                Name = mailInputEntity.Name,
                SenderTest = mailInputEntity.SenderTest,
                OkCode = mailInputEntity.OkCode,
                OkTest = mailInputEntity.OkTest,
                FailCode = mailInputEntity.FailCode,
                FailTest = mailInputEntity.FailTest,
                DefaultCode = mailInputEntity.DefaultCode,  
                ContentTest = mailInputEntity.ContentTest,
                SensorOutputId = mailInputEntity.SensorOutputId,
            };
        }

        public static MailInputEntity ToEntity(this MailInputPoco mailInputPoco, bool isNew)
        {
            var mailInputEntity = new MailInputEntity()
            {
                Id=mailInputPoco.Id,
                Enabled=mailInputPoco.Enabled,
                Name = mailInputPoco.Name,
                SensorOutputId=mailInputPoco.SensorOutputId,
                OkCode = mailInputPoco.OkCode,
                OkTest = mailInputPoco.OkTest,
                FailCode = mailInputPoco.FailCode,
                FailTest = mailInputPoco.FailTest,
                DefaultCode = mailInputPoco.DefaultCode,
                ContentTest = mailInputPoco.ContentTest,
                SenderTest =mailInputPoco.SenderTest,
            };

            if (!isNew)
            {
                mailInputEntity.Id = mailInputPoco.Id;
            }
            else
            {
                mailInputEntity.Id = Guid.NewGuid();
            }

            return mailInputEntity;
        }

    }
}

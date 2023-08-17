using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class MailInputPocoExtensions
    {

        public static MailInputRulePoco ToPoco(this MailInputRuleEntity mailInputEntity)
        {
            return new MailInputRulePoco
            {
                Id = mailInputEntity.Id,
                Enabled = mailInputEntity.Enabled,
                Name = mailInputEntity.Name,
                FromTest = mailInputEntity.FromTest,
                OkCode = mailInputEntity.OkCode,
                OkTest = mailInputEntity.OkTest,
                FailCode = mailInputEntity.FailCode,
                FailTest = mailInputEntity.FailTest,
                DefaultCode = mailInputEntity.DefaultCode,  
                BodyTest = mailInputEntity.BodyTest,
                SensorOutputId = mailInputEntity.SensorOutputId,
                SubjectTest = mailInputEntity.SubjectTest,
            };
        }

        public static MailInputRuleEntity ToEntity(this MailInputRulePoco mailInputPoco, bool isNew)
        {
            var mailInputEntity = new MailInputRuleEntity()
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
                BodyTest = mailInputPoco.BodyTest,
                FromTest =mailInputPoco.FromTest,
                SubjectTest = mailInputPoco.SubjectTest,
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

﻿using PNMTD.Models.Poco;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("mailinputrules")]
    public class MailInputRuleEntity
    {

        [Key]
        public Guid Id { get; set; }

        public bool Enabled { get; set; }
        public string Name { get; set; }

        public string? BodyTest { get; set; }

        public string? FromTest { get; set; }

        public string? SubjectTest { get; set; }

        public int? OkCode { get; set; }

        public string? OkTest { get; set; }

        public int? FailCode { get; set;}

        public string? FailTest { get; set; }

        public int DefaultCode { get; set; }

        public virtual SensorEntity? SensorOutput { get; set; }

        public Guid? SensorOutputId { get; set; }
    }
}

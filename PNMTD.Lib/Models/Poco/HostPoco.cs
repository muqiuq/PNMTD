
namespace PNMTD.Lib.Models.Poco
{
    public class HostPoco
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? Location { get; set; }

        public string? Notes { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        public List<Guid> Sensors { get; set; } = new List<Guid> { };

        public override bool Equals(object? obj)
        {
            var other = obj as HostPoco;
            return other?.Id == Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}

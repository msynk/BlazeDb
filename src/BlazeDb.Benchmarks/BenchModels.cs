using BlazeDb;

namespace BlazeDb.Benchmarks;

[Table("people")]
public partial class BenchPerson
{
    [Key]
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [Index]
    public bool Flag { get; set; }

    [OrderedIndex]
    public int Age { get; set; }
}

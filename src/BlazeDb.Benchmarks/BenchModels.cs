using BlazeDb;

namespace BlazeDb.Benchmarks;

[BlazeDbTable("people")]
public partial class BenchPerson
{
    [BlazeDbKey]
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [BlazeDbIndex]
    public bool Flag { get; set; }

    [BlazeDbOrderedIndex]
    public int Age { get; set; }
}

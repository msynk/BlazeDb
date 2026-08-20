using Microsoft.CodeAnalysis;

namespace BlazeDb.SourceGen;

internal sealed class BlazeDbDiagnosticInfo
{
    public BlazeDbDiagnosticInfo(DiagnosticDescriptor descriptor, Location? location, params object[] args)
    {
        Descriptor = descriptor;
        Location = location;
        Args = args;
    }

    public DiagnosticDescriptor Descriptor { get; }

    public Location? Location { get; }

    public object[] Args { get; }
}

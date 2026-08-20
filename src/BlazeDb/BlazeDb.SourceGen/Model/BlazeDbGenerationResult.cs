using System.Collections.Generic;

namespace BlazeDb.SourceGen;

internal sealed class BlazeDbGenerationResult
{
    public BlazeDbTableModel? Model;
    public List<BlazeDbDiagnosticInfo> Diagnostics = new List<BlazeDbDiagnosticInfo>();
}

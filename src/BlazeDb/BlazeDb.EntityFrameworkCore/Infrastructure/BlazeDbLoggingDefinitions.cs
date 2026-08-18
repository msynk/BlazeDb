using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazeDb.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The provider emits no log events of its own — there is no command to log, no connection to
/// open and no round trip to time — so this exists only to satisfy the service contract.
/// </summary>
public sealed class BlazeDbLoggingDefinitions : LoggingDefinitions;

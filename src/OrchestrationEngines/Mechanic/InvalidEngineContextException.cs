using System;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// Exception thrown when an engine context that has been computed, is null, which is invalid.
/// By throwing this exception, we know it is intentional.
/// </summary>
[Serializable]
internal class InvalidEngineContextException : Exception
{
  public InvalidEngineContextException()
  {
  }

  public InvalidEngineContextException(string message) : base(message)
  {
  }

}
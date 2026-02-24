namespace ClaudeCommandCenter.Models;

public class FlowCancelledException(string status = "Cancelled") : Exception(status);

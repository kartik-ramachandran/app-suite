namespace ContentStudio.Api.Models;

public class ChainEdge
{
    public int Id { get; set; }
    public Guid ChainId { get; set; }
    public PromptChain? Chain { get; set; }
    public int SourceNodeId { get; set; }
    public ChainNode? SourceNode { get; set; }
    public int TargetNodeId { get; set; }
    public ChainNode? TargetNode { get; set; }
    public string? Label { get; set; }
    public string? ConditionBranch { get; set; }
}

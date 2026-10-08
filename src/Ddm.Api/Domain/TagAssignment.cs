namespace Ddm.Api.Domain;

/// <summary>Attaches a tag to any item. Polymorphic, so there is no foreign key to the item itself.</summary>
public class TagAssignment
{
    public Guid TagId { get; set; }
    public ItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
}

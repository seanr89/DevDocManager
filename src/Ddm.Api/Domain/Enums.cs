namespace Ddm.Api.Domain;

public enum Visibility { Private = 1, Internal = 2 }
public enum Role { Reader = 1, Editor = 2, Admin = 3 }
public enum ItemType { Document = 1, Spec = 2, Asset = 3, Project = 4 }
public enum TokenScope { Read = 1, Write = 2 }

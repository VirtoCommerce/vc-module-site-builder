export interface PageDesignerContext {
  groupId?: string | null;
  storeId?: string | null;
  cultureName?: string | null;
  status?: string | null;
}

export function canOpenPageDesigner(
  context: PageDesignerContext,
): context is PageDesignerContext & { groupId: string; storeId: string } {
  return Boolean(context.groupId?.trim() && context.storeId?.trim() && context.status !== "Archived");
}

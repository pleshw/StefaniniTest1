export type OrderAction = 'confirm' | 'hold' | 'block' | 'cancel';

/** The actions Orders accepts for an order in each status; any other is refused with 409. */
const actionsByStatus: Readonly<Record<string, readonly OrderAction[]>> = {
  Pending: ['confirm', 'hold', 'block'],
  OnHold: ['confirm', 'block'],
  Confirmed: ['cancel'],
};

export function actionsFor(status: string): readonly OrderAction[] {
  return actionsByStatus[status] ?? [];
}

/** Orders requires a reason to hold, block and cancel; confirming takes one only if given. */
export function reasonRequired(action: OrderAction): boolean {
  return action !== 'confirm';
}

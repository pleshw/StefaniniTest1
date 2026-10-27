import type { HttpClient } from '../../shared/api/http';
import type { Quote } from '../../shared/api/orders';

/** A stored version of a discount rule. Parameters and conditions are the JSON Orders stores. */
export interface DiscountRule {
  readonly id: string;
  readonly version: number;
  readonly name: string;
  readonly type: string;
  readonly parameters: unknown;
  readonly conditions: unknown;
  readonly priority: number;
  readonly stackable: boolean;
  readonly oncePerClient: boolean;
  readonly validFrom: string;
  readonly validTo: string | null;
  readonly isActive: boolean;
  readonly createdBy: string;
  readonly createdAt: string;
}

/** A stored version of a confirmation rule. */
export interface ConfirmationRule {
  readonly id: string;
  readonly version: number;
  readonly name: string;
  readonly action: string;
  readonly conditions: unknown;
  readonly priority: number;
  readonly isActive: boolean;
  readonly createdBy: string;
  readonly createdAt: string;
}

export interface AppliedDiscount {
  readonly ruleId: string;
  readonly ruleVersion: number;
  readonly amount: number;
}

export interface RulePreview {
  readonly quote: Quote;
  readonly applied: readonly AppliedDiscount[];
}

/** The sample cart a rule is tried on. */
export interface PreviewCart {
  readonly currency: string;
  readonly items: readonly { readonly sku: string; readonly quantity: number }[];
}

const discountPath = '/bff/admin/discount-rules';
const confirmationPath = '/bff/admin/confirmation-rules';

export function listDiscountRules(
  http: HttpClient,
  active?: boolean,
  signal?: AbortSignal,
): Promise<readonly DiscountRule[]> {
  return http.get<readonly DiscountRule[]>(
    discountPath,
    active === undefined ? undefined : { active },
    signal,
  );
}

export async function createDiscountRule(http: HttpClient, body: object): Promise<DiscountRule> {
  return (await http.send<DiscountRule>(discountPath, { method: 'POST', body })).data;
}

/** Stores the next version of a rule; `version` is the one being replaced (If-Match). */
export async function replaceDiscountRule(
  http: HttpClient,
  id: string,
  version: number,
  body: object,
): Promise<DiscountRule> {
  return (
    await http.send<DiscountRule>(`${discountPath}/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body,
      ifMatch: version,
    })
  ).data;
}

export async function deactivateDiscountRule(
  http: HttpClient,
  id: string,
  version: number,
): Promise<DiscountRule> {
  return (
    await http.send<DiscountRule>(`${discountPath}/${encodeURIComponent(id)}/deactivate`, {
      method: 'POST',
      ifMatch: version,
    })
  ).data;
}

/** Prices the sample cart with a rule that is not stored yet (or the stored rule `ruleId` replaced by it). */
/** Prices a sample cart with the active rules, plus `rule` (in place of `ruleId`'s stored version) when given. */
export async function previewDiscountRule(
  http: HttpClient,
  cart: PreviewCart,
  rule?: object,
  ruleId?: string,
): Promise<RulePreview> {
  const body = {
    ...cart,
    ...(rule === undefined ? {} : { rule }),
    ...(ruleId === undefined ? {} : { ruleId }),
  };

  return (await http.send<RulePreview>(`${discountPath}/preview`, { method: 'POST', body })).data;
}

export function listConfirmationRules(
  http: HttpClient,
  signal?: AbortSignal,
): Promise<readonly ConfirmationRule[]> {
  return http.get<readonly ConfirmationRule[]>(confirmationPath, undefined, signal);
}

export async function createConfirmationRule(
  http: HttpClient,
  body: object,
): Promise<ConfirmationRule> {
  return (await http.send<ConfirmationRule>(confirmationPath, { method: 'POST', body })).data;
}

export async function replaceConfirmationRule(
  http: HttpClient,
  id: string,
  version: number,
  body: object,
): Promise<ConfirmationRule> {
  return (
    await http.send<ConfirmationRule>(`${confirmationPath}/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body,
      ifMatch: version,
    })
  ).data;
}

export async function deactivateConfirmationRule(
  http: HttpClient,
  id: string,
  version: number,
): Promise<ConfirmationRule> {
  return (
    await http.send<ConfirmationRule>(`${confirmationPath}/${encodeURIComponent(id)}/deactivate`, {
      method: 'POST',
      ifMatch: version,
    })
  ).data;
}

declare global {
  interface Window { __API_URL__?: string }
}

const BASE = (window.__API_URL__ || 'http://localhost:8080').replace(/\/+$/, '');

export type ClassInfo = { id: string; name: string; totalSeats: number; heldSeats: number; confirmedSeats: number; availableSeats: number };
export type Status = 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';
export type Hold = { holdId: string; classId: string; className: string; status: Status; expiresAt: string };

let clockOffset = 0; // server time minus local time, in ms
export const getOffset = () => clockOffset;

export class ApiError extends Error {
  status: number;
  code: string;
  constructor(status: number, code: string, message: string) { super(message); this.status = status; this.code = code; }
}

async function call<T>(path: string, method = 'GET', opts: { student?: string; body?: unknown } = {}): Promise<T> {
  const headers: Record<string, string> = {};
  if (opts.student) headers['X-Student-Id'] = opts.student;
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  let res: Response;
  try {
    res = await fetch(BASE + path, { method, headers, body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined });
  } catch {
    throw new ApiError(0, 'NETWORK', 'সার্ভারের সাথে যোগাযোগ করা যাচ্ছে না। সার্ভার চালু আছে কিনা দেখে আবার চেষ্টা করুন।');
  }
  const serverTime = Number(res.headers.get('X-Server-Time'));
  if (serverTime) clockOffset = serverTime - Date.now();
  if (res.status === 204) return undefined as T;
  let data: any = null;
  try { data = await res.json(); } catch { /* not json */ }
  if (!res.ok) throw new ApiError(res.status, data?.code ?? 'ERROR', data?.error ?? 'কিছু একটা সমস্যা হয়েছে। আবার চেষ্টা করুন।');
  return data as T;
}

export const listClasses = () => call<ClassInfo[]>('/classes');
export const createClass = (name: string, totalSeats: number) => call('/classes', 'POST', { body: { name, totalSeats } });
export const apply = (classId: string, student: string) => call(`/classes/${classId}/holds`, 'POST', { student });
export const myHolds = (student: string) => call<Hold[]>('/me/holds', 'GET', { student });
export const pay = (holdId: string, student: string) => call(`/holds/${holdId}/confirm`, 'POST', { student });
export const cancel = (holdId: string, student: string) => call(`/holds/${holdId}`, 'DELETE', { student });

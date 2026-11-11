import { useCallback, useEffect, useState } from 'react';
import * as api from './api';
import type { ClassInfo, Hold } from './api';

const POLL_MS = 3000;
const ID_RE = /^\d{17}$/;
const STATUS_BN: Record<string, string> = {
  HELD: 'অপেক্ষমাণ (ফি বাকি)',
  CONFIRMED: 'ভর্তি নিশ্চিত',
  EXPIRED: 'মেয়াদ শেষ',
  CANCELLED: 'বাতিল',
};

const errText = (e: unknown) => (e instanceof Error ? e.message : 'অজানা সমস্যা হয়েছে।');
const fmt = (ms: number) => {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(s / 60)} মিনিট ${s % 60} সেকেন্ড`;
};
const saved = () => { try { return localStorage.getItem('studentId') ?? ''; } catch { return ''; } };

function useNow() {
  const [n, setN] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setN(Date.now()), 1000); return () => clearInterval(t); }, []);
  return n;
}

type Notice = { kind: 'ok' | 'err'; text: string } | null;

export default function App() {
  const [studentId, setStudentId] = useState(saved());
  const [draft, setDraft] = useState(saved());
  const [tab, setTab] = useState<'classes' | 'mine' | 'admin'>('classes');
  const [classes, setClasses] = useState<ClassInfo[]>([]);
  const [holds, setHolds] = useState<Hold[]>([]);
  const [notice, setNotice] = useState<Notice>(null);
  const [netErr, setNetErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState('');
  const [seats, setSeats] = useState('');
  const now = useNow() + api.getOffset();
  const valid = ID_RE.test(studentId);

  const loadClasses = useCallback(async () => {
    try { setClasses(await api.listClasses()); setNetErr(null); } catch (e) { setNetErr(errText(e)); }
  }, []);
  const loadHolds = useCallback(async () => {
    if (!ID_RE.test(studentId)) { setHolds([]); return; }
    try { setHolds(await api.myHolds(studentId)); setNetErr(null); } catch (e) { setNetErr(errText(e)); }
  }, [studentId]);

  useEffect(() => {
    loadClasses();
    const t = setInterval(loadClasses, POLL_MS);
    return () => clearInterval(t);
  }, [loadClasses]);
  useEffect(() => {
    loadHolds();
    const t = setInterval(loadHolds, POLL_MS);
    return () => clearInterval(t);
  }, [loadHolds]);

  const saveId = () => {
    const v = draft.trim();
    if (!ID_RE.test(v)) { setNotice({ kind: 'err', text: 'জন্ম নিবন্ধন নম্বর অবশ্যই ১৭ অঙ্কের সংখ্যা হতে হবে (ইংরেজি অঙ্কে)।' }); return; }
    try { localStorage.setItem('studentId', v); } catch { /* ignore */ }
    setStudentId(v);
    setNotice({ kind: 'ok', text: 'জন্ম নিবন্ধন নম্বর সংরক্ষিত হয়েছে।' });
  };

  const run = async (fn: () => Promise<void>, ok?: string) => {
    setBusy(true); setNotice(null);
    try { await fn(); if (ok) setNotice({ kind: 'ok', text: ok }); }
    catch (e) { setNotice({ kind: 'err', text: errText(e) }); }
    finally { await Promise.all([loadClasses(), loadHolds()]); setBusy(false); }
  };

  const eff = (h: Hold) => (h.status === 'HELD' && Date.parse(h.expiresAt) <= now ? 'EXPIRED' : h.status);

  return (
    <div className="wrap">
      <h1>স্কুল ভর্তি</h1>

      <div className="card">
        <label>শিক্ষার্থীর জন্ম নিবন্ধন নম্বর (১৭ অঙ্ক)</label>
        <div className="row">
          <input value={draft} onChange={(e) => setDraft(e.target.value)} inputMode="numeric" maxLength={17} placeholder="যেমন: 20152691234567890" />
          <button onClick={saveId}>সংরক্ষণ</button>
        </div>
        {!valid && <p className="hint">আবেদন করতে আগে জন্ম নিবন্ধন নম্বর সংরক্ষণ করুন।</p>}
      </div>

      {netErr && <div className="notice err">{netErr}</div>}
      {notice && <div className={`notice ${notice.kind}`}>{notice.text}</div>}

      <div className="tabs">
        <button className={tab === 'classes' ? 'on' : ''} onClick={() => setTab('classes')}>ক্লাস তালিকা</button>
        <button className={tab === 'mine' ? 'on' : ''} onClick={() => setTab('mine')}>আমার আবেদন</button>
        <button className={tab === 'admin' ? 'on' : ''} onClick={() => setTab('admin')}>ক্লাস তৈরি (অ্যাডমিন)</button>
      </div>

      {tab === 'classes' && (
        <div>
          {classes.length === 0 && !netErr && <p className="hint">এখনো কোনো ক্লাস তৈরি হয়নি।</p>}
          {classes.map((c) => (
            <div className="card" key={c.id}>
              <h3>{c.name}</h3>
              <p>মোট আসন: <b>{c.totalSeats}</b> · অপেক্ষমাণ: <b>{c.heldSeats}</b> · নিশ্চিত: <b>{c.confirmedSeats}</b> · খালি: <b>{c.availableSeats}</b></p>
              <button
                disabled={busy || !valid || c.availableSeats === 0}
                onClick={() => run(async () => { await api.apply(c.id, studentId); setTab('mine'); }, 'আবেদন সফল হয়েছে। মেয়াদ শেষ হওয়ার আগে ভর্তি ফি দিন।')}
              >
                {c.availableSeats === 0 ? 'আসন শেষ' : 'আবেদন করুন'}
              </button>
            </div>
          ))}
        </div>
      )}

      {tab === 'mine' && (
        <div>
          {!valid && <p className="hint">আপনার আবেদন দেখতে জন্ম নিবন্ধন নম্বর সংরক্ষণ করুন।</p>}
          {valid && holds.length === 0 && <p className="hint">আপনার কোনো আবেদন নেই।</p>}
          {holds.map((h) => {
            const st = eff(h);
            return (
              <div className="card" key={h.holdId}>
                <h3>{h.className}</h3>
                <p>অবস্থা: <span className={`badge ${st}`}>{STATUS_BN[st]}</span></p>
                {st === 'HELD' && <p>মেয়াদ শেষ হতে বাকি: <b>{fmt(Date.parse(h.expiresAt) - now)}</b></p>}
                {st === 'HELD' && (
                  <div className="row">
                    <button disabled={busy} onClick={() => run(() => api.pay(h.holdId, studentId) as Promise<void>, 'ভর্তি ফি জমা হয়েছে, ভর্তি নিশ্চিত।')}>ভর্তি ফি দিন</button>
                    <button className="sec" disabled={busy} onClick={() => run(() => api.cancel(h.holdId, studentId), 'আবেদন বাতিল হয়েছে।')}>বাতিল</button>
                  </div>
                )}
                {st === 'EXPIRED' && <p className="hint">মেয়াদ শেষ। ভর্তি ফি দেওয়া যাবে না; চাইলে আবার আবেদন করুন।</p>}
              </div>
            );
          })}
        </div>
      )}

      {tab === 'admin' && (
        <div className="card">
          <h3>নতুন ক্লাস (শিফট)</h3>
          <label>ক্লাসের নাম</label>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Class 6 - Morning Shift" />
          <label>আসন সংখ্যা</label>
          <input value={seats} onChange={(e) => setSeats(e.target.value)} inputMode="numeric" placeholder="120" />
          <button
            disabled={busy}
            onClick={() => run(async () => {
              const n = Number(seats);
              if (!name.trim() || !Number.isInteger(n) || n < 1) throw new Error('ক্লাসের নাম দিন এবং আসন সংখ্যা ১ বা তার বেশি পূর্ণসংখ্যা দিন।');
              await api.createClass(name.trim(), n);
              setName(''); setSeats('');
            }, 'ক্লাস তৈরি হয়েছে।')}
          >তৈরি করুন</button>
        </div>
      )}
    </div>
  );
}

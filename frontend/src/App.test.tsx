import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, beforeEach, describe, it, expect } from 'vitest';
import App from './App';
import * as api from './api';

vi.mock('./api', () => ({
  listClasses: vi.fn(),
  createClass: vi.fn(),
  apply: vi.fn(),
  myHolds: vi.fn(),
  pay: vi.fn(),
  cancel: vi.fn(),
  getOffset: vi.fn(),
}));

const m = api as unknown as Record<string, ReturnType<typeof vi.fn>>;
const STUDENT = '20152691234567890';
const cls = (over: object = {}) => ({ id: 'c_1', name: 'Class 6 - Morning Shift', totalSeats: 10, heldSeats: 0, confirmedSeats: 0, availableSeats: 5, ...over });

beforeEach(() => {
  vi.resetAllMocks();
  localStorage.clear();
  m.getOffset.mockReturnValue(0);
  m.listClasses.mockResolvedValue([]);
  m.myHolds.mockResolvedValue([]);
});

describe('App', () => {
  it('rejects a birth registration number that is not 17 digits', async () => {
    render(<App />);
    await userEvent.type(screen.getByPlaceholderText(/20152691234567890/), '123');
    await userEvent.click(screen.getByText('সংরক্ষণ'));
    expect(await screen.findByText(/১৭ অঙ্কের সংখ্যা/)).toBeInTheDocument();
    expect(localStorage.getItem('studentId')).toBeNull();
  });

  it('shows seat counts and disables apply when a class is full or no id is saved', async () => {
    localStorage.setItem('studentId', STUDENT);
    m.listClasses.mockResolvedValue([cls(), cls({ id: 'c_2', name: 'Class 6 - Day Shift', availableSeats: 0, confirmedSeats: 10 })]);
    render(<App />);
    expect(await screen.findByText('Class 6 - Morning Shift')).toBeInTheDocument();
    expect(screen.getByText('আবেদন করুন')).toBeEnabled();
    expect(screen.getByText('আসন শেষ')).toBeDisabled();
  });

  it('shows a readable message when applying fails because seats ran out', async () => {
    localStorage.setItem('studentId', STUDENT);
    m.listClasses.mockResolvedValue([cls()]);
    m.apply.mockRejectedValue(new Error('দুঃখিত, এই ক্লাসে আর কোনো আসন খালি নেই।'));
    render(<App />);
    await userEvent.click(await screen.findByText('আবেদন করুন'));
    expect(await screen.findByText('দুঃখিত, এই ক্লাসে আর কোনো আসন খালি নেই।')).toBeInTheDocument();
  });

  it('shows an expired hold as EXPIRED without a pay button (no reload needed)', async () => {
    localStorage.setItem('studentId', STUDENT);
    m.myHolds.mockResolvedValue([
      { holdId: 'h_1', classId: 'c_1', className: 'Class 6 - Morning Shift', status: 'HELD', expiresAt: new Date(Date.now() - 5000).toISOString() },
    ]);
    render(<App />);
    await userEvent.click(screen.getByText('আমার আবেদন'));
    expect(await screen.findByText('মেয়াদ শেষ')).toBeInTheDocument();
    expect(screen.queryByText('ভর্তি ফি দিন')).not.toBeInTheDocument();
  });

  it('shows pay and cancel buttons for a live hold and calls pay with the saved student id', async () => {
    localStorage.setItem('studentId', STUDENT);
    m.myHolds.mockResolvedValue([
      { holdId: 'h_1', classId: 'c_1', className: 'Class 6 - Morning Shift', status: 'HELD', expiresAt: new Date(Date.now() + 600000).toISOString() },
    ]);
    m.pay.mockResolvedValue({ holdId: 'h_1', status: 'CONFIRMED' });
    render(<App />);
    await userEvent.click(screen.getByText('আমার আবেদন'));
    expect(await screen.findByText('বাতিল')).toBeInTheDocument();
    await userEvent.click(screen.getByText('ভর্তি ফি দিন'));
    expect(m.pay).toHaveBeenCalledWith('h_1', STUDENT);
  });
});

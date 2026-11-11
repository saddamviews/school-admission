# Assumptions

1. **Hold duration:** default 600 সেকেন্ড (১০ মিনিট), `HOLD_DURATION_SECONDS` দিয়ে বদলানো যায়। Spec-এ default দেওয়া আছে।
2. **Duplicate আবেদন:** একই শিক্ষার্থী একই class-এ একসাথে একটির বেশি HELD/CONFIRMED রাখতে পারে না → `409 DUPLICATE_APPLICATION`। বাতিল বা মেয়াদোত্তীর্ণ হলে আবার আবেদন করা যায়। DB-তে partial unique index এটা নিশ্চিত করে।
3. **একাধিক class/shift:** একই শিক্ষার্থী ভিন্ন class-এ আবেদন করতে পারে (spec শুধু "একই class" সীমিত করেছে)।
4. **Seat পূর্ণ:** `409 SEAT_FULL`। অন্যান্য code: `400` ভুল input/Student-Id, `404` পাওয়া যায়নি (অন্যের hold-ও 404, তথ্য ফাঁস এড়াতে), `410` মেয়াদ শেষ (confirm/cancel), `409` বাতিল বা confirmed হয়ে যাওয়া hold-এ ভুল action।
5. **Cancel:** শুধু live HELD বাতিল করা যায়; seat সাথে সাথে ফেরত আসে। আবার DELETE দিলে 204 (idempotent)। CONFIRMED বাতিল করা যায় না (409) কারণ refund flow নেই।
6. **Confirm idempotent:** আগেই CONFIRMED হলে আবার 200, `confirmedSeats` বাড়ে না।
7. **জন্ম নিবন্ধন নম্বর validation:** ঠিক ১৭টি ইংরেজি অঙ্ক, এর বেশি কিছু (checksum, জন্মসাল) যাচাই করা হয় না। Login নেই, তাই এটা শুধু পরিচয়-চিহ্ন।
8. **সময়:** মেয়াদ ও seat হিসাব সব DB server-এর ঘড়ি (`now()`) থেকে, ISO 8601 UTC। ব্রাউজারের countdown ভুল ঘড়িতে বিভ্রান্ত না হয়, তাই প্রতিটি response-এ `X-Server-Time` header পাঠানো হয় এবং UI সেই পার্থক্য হিসাব করে নেয়।
9. **Class তৈরি:** admin login নেই, UI-এর "ক্লাস তৈরি" ট্যাব যে কেউ ব্যবহার করতে পারে। নাম unique বাধ্যতামূলক নয়। seat সংখ্যা ≥ 1।
10. **UI:** "আবেদন" screen আলাদা পেজ না করে "আমার আবেদন" তালিকার প্রতিটি কার্ডে (অবস্থা, countdown, "ভর্তি ফি দিন", "বাতিল") দেখানো হয়েছে। সংখ্যা ৩ সেকেন্ড পরপর polling-এ হালনাগাদ হয়, countdown প্রতি সেকেন্ডে।
11. **Seat গণনা:** `held` = `HELD` ও `expires_at > now()`; `confirmed` = `CONFIRMED`; `available = total - held - confirmed`।

## Bonus: একাধিক backend process কীভাবে নিরাপদ
Overbooking-এর সুরক্ষা application memory-তে নয়, PostgreSQL-এ: hold তৈরির transaction শুরুতেই `SELECT ... FOR UPDATE` দিয়ে class row lock করে। দুটো process একই class-এ আসলে একটি অপরটির commit পর্যন্ত অপেক্ষা করে, তারপর committed data দেখে count করে। Confirm/cancel একক atomic conditional `UPDATE ... WHERE status='HELD' AND expires_at > now()`। Schema init-এ `pg_advisory_lock` দিয়ে দুই process-এর race ঠেকানো হয়েছে।

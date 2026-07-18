# Security notes — Claude Usage Widget

สรุปผลตรวจความปลอดภัย (self-audit) สำหรับผู้ที่จะ build/แจกต่อ

## หลักการออกแบบ

- **ไม่มี network listener** — แอปไม่เปิด port ใดๆ จึงไม่มีพื้นผิวให้โจมตีจากเครือข่าย
  การเชื่อมต่อทั้งหมดเป็นขาออก (outbound) ไปที่ `api.anthropic.com` ผ่าน TLS 1.2 เท่านั้น
- **ไม่ตาม redirect** — request ตั้ง `AllowAutoRedirect = false` และถือว่า 3xx เป็น error
  เพราะ `HttpWebRequest` จะส่ง header `Authorization: Bearer` ซ้ำไป host ปลายทางของ redirect
  การปิด redirect จึง **บังคับด้วยโค้ดจริง** ว่า token ออกได้ที่ `api.anthropic.com` เท่านั้น
- **อ่าน token แบบ read-only** จาก `%USERPROFILE%\.claude\.credentials.json`
  (ยกเว้นฟีเจอร์สลับบัญชีที่ผู้ใช้สั่งเอง — ดูด้านล่าง) token ไม่ถูก log และไม่ส่งไปที่อื่น
- **ไม่มี telemetry / analytics** — ไม่มีการส่งข้อมูลไปเซิร์ฟเวอร์ของผู้พัฒนา
- **ไม่ render เนื้อหาจากเว็บ** — ไม่มี WebView/HTML rendering ที่จะโดน XSS/RCE
- input จากภายนอกมีแค่ 2 อย่าง: JSON response ของ Anthropic (parse เฉพาะ `limits[]`
  ที่เป็นโครงสร้างกลาง) และไฟล์ในเครื่องผู้ใช้เอง

## พื้นผิวที่ตรวจและผลลัพธ์

| พื้นผิว | สถานะ |
|---|---|
| Path traversal (pet image, credentials) | ปลอดภัย — path มาจาก `OpenFileDialog` (ผู้ใช้เลือกเอง) หรือ path ที่แอปสร้าง (timestamp) ไม่มี path จากภายนอกที่ควบคุมไม่ได้ |
| Command injection (`cmd.exe` เปิด login) | ปลอดภัย — `CLAUDE_CONFIG_DIR` ใส่ใน `set "VAR=..."` ที่ quote แล้ว และค่าเป็น path ที่แอปสร้างเอง ไม่รับ input จากผู้ใช้ |
| Binary planting / PATH hijack (`claude` ใน cmd) | แก้แล้ว — ตั้ง `WorkingDirectory` เป็น dir ที่แอปสร้างเอง กัน `cmd` resolve `claude` จาก CWD เดิม (เช่น Downloads) ที่อาจมี `claude.bat` ปลอมวางไว้ |
| Token leak ผ่าน HTTP redirect | แก้แล้ว — `AllowAutoRedirect = false` + ปฏิเสธ 3xx กัน Bearer token ถูกส่งต่อไป host อื่น |
| JSON parse (usage response) | ปลอดภัย — ใช้ `JavaScriptSerializer`, parse เฉพาะ field ที่รู้จัก, field ผิดรูปถูกข้าม; label ว่างถูก guard ไม่ให้ index crash |
| Response ขนาดใหญ่ (memory DoS) | แก้แล้ว — จำกัดการอ่าน body network ที่ 1 MB (`ReadCapped`) response ประดิษฐ์ขนาดยักษ์ไม่ทำให้ OOM |
| สลับบัญชีด้วยไฟล์ปลอม | แก้แล้ว — validate ว่าเป็น credentials ที่ใช้ได้ (`LoadFrom != null`) ก่อนคัดลอกทับ token จริง |
| Untrusted image → `Bitmap` | ยอมรับความเสี่ยงต่ำ — GDI+ เคยมี CVE จากไฟล์ภาพประดิษฐ์ แต่ผู้ใช้เลือกไฟล์เอง ไม่ใช่ไฟล์จากภายนอก |
| Credential backup สะสม | แก้แล้ว — เก็บเฉพาะ 5 ไฟล์ล่าสุด, ลบที่เหลืออัตโนมัติ |
| Resource leak (Image/Timer) | จัดการแล้ว — dispose ภาพเดิมก่อนโหลดใหม่ |
| Account list injection (path มี `;`) | แก้แล้ว — ปฏิเสธ path ที่มี `;` (ตัวคั่นรายการบัญชี) กันรายการเพี้ยน |
| Secrets/token hardcode ในซอร์ส | ไม่มี — สแกนทั้งไฟล์แล้วสะอาด, network ออกที่ `api.anthropic.com` โฮสต์เดียว |
| ข้อมูลส่วนตัวหลุดใน git | ไม่มี — `.gitignore` กัน exe/creds/history/settings; git history สะอาดตั้งแต่ commit แรก |

## ข้อจำกัดที่ผู้ใช้ควรรู้ (ไม่ใช่ช่องโหว่ของแอป)

- ไฟล์ `.credentials.json` เป็น **plaintext** ตามที่ Claude Code เก็บไว้เอง —
  ถ้ามีมัลแวร์ในเครื่องอยู่แล้ว มันอ่าน token ได้โดยตรงไม่ว่าจะมี widget หรือไม่
  widget ไม่ได้เพิ่มความเสี่ยงตรงนี้
- **ฟีเจอร์สลับบัญชี**คัดลอกไฟล์ credentials ทับ `~\.claude\.credentials.json`
  (สำรองก่อนเสมอ + เก็บ token ล่าสุดของบัญชีเก่ากลับ slot) — เป็นการเขียนไฟล์ที่ผู้ใช้
  สั่งเอง ไม่ใช่อัตโนมัติ backup ทั้งหมดอยู่ใน `%LOCALAPPDATA%\ClaudeUsageWidget\`
- exe **ไม่ได้ code-sign** — Windows SmartScreen จะเตือนตอนรันครั้งแรก (ปกติของ exe
  โอเพนซอร์สที่ยังไม่ซื้อใบรับรอง) ผู้รับควร build เองจาก source เพื่อความมั่นใจสูงสุด

## กระบวนการตรวจ

โค้ดผ่านการ audit หลายรอบด้วยหลายโมเดล — รอบล่าสุดตรวจเจอและแก้: token leak ผ่าน
HTTP redirect, binary-planting ผ่าน `claude` ใน cmd, crash จาก response ประดิษฐ์,
memory DoS จาก response ขนาดใหญ่, และการ validate ไฟล์ก่อนสลับบัญชี ทั้งหมดแก้แล้ว
ในเวอร์ชันปัจจุบัน (ดูตารางด้านบน) — build เองจาก source เพื่อยืนยันได้เสมอ

## กิน token ไหม?

**ไม่กินเลย** — endpoint `/api/oauth/usage` เป็นการอ่านตัวเลขโควต้า ไม่นับเข้า plan
กราฟ/ราคา/สถิติทั้งหมดคำนวณจากไฟล์ในเครื่อง ไม่มีการเรียกโมเดล

## English summary

Multi-model audit result: no network listener (outbound-only to `api.anthropic.com`
over TLS, `AllowAutoRedirect=false` so the Bearer token can't be forwarded off-host),
token read-only, no telemetry, no web rendering. Reviewed for path traversal, command
injection, binary planting (`WorkingDirectory` pinned so `claude` can't resolve from a
planted CWD binary), redirect token-leak, untrusted-response crash/DoS (label guard +
1 MB read cap), and account-file validation before overwriting live credentials — all
mitigated. Residual notes: the Claude
Code credentials file is plaintext (not this app's doing); the account-switch feature
writes credentials only on explicit user action, with timestamped backups pruned to 5;
the exe is unsigned (build from source to verify). Reading `/usage` does not consume
plan quota.

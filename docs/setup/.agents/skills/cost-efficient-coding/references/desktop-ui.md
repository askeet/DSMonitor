# Desktop UI validation

Apply to a verified WPF or other desktop application. Prefer automated tests, application logs, process checks and platform-appropriate programmatic UI automation. Do not request interactive computer/screen access merely to validate routine Start/Stop behavior. Do not open any interactive computer-use session without explicit user authorization. Any test launching an application must retain the identity of the process it started and ensure controlled cleanup, including on failure; terminate only that test-owned process, never unrelated user processes.

Use screenshots or interactive visual inspection only when visual layout, rendering or usability cannot be adequately verified programmatically. Perform actual visual inspection when required; if unavailable or unauthorized, report visual validation as pending manual verification. Never treat log-based verification as proof of correct rendering or claim that pending visual checks passed.

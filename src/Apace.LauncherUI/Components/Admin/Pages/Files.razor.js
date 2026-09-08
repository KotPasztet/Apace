// Files page browser-side requests. Kept as a colocation module (imported from
// Files.razor) so it also loads after in-circuit navigation, unlike inline <script> tags.
// The save POST always carries the antiforgery token that was rendered during
// prerendering (same pattern as PasskeySubmit) - it is never sent token-less.

export async function request(url, method, body, token) {
    const init = { method: method, headers: {} };
    if (token) init.headers['X-XSRF-TOKEN'] = token;
    if (body !== null && body !== undefined) {
        init.headers['Content-Type'] = 'application/json';
        init.body = body;
    }
    try {
        const response = await fetch(url, init);
        return { status: response.status, text: await response.text() };
    } catch (err) {
        return { status: 0, text: 'Network error: ' + err };
    }
}

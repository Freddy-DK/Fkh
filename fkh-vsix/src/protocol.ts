/** Protocol version for the Fkh client-backend communication. */
export const PROTOCOL_VERSION = 1;

/** Client identifier sent to the backend. */
export const CLIENT_APP = 'VS Code extension';

/** Standard headers to include in all backend requests. */
export function getProtocolHeaders(): Record<string, string> {
  return {
    'X-Fkh-Protocol-Version': String(PROTOCOL_VERSION),
    'X-Fkh-Client': CLIENT_APP,
  };
}

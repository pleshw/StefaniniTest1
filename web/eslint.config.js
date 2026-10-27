import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';

// Layering (same rule as the back end): ui may import domain and api, never the reverse.
const noUiImports = {
  'no-restricted-imports': [
    'error',
    {
      patterns: [
        { group: ['**/ui', '**/ui/**'], message: 'domain and api code must not import ui code.' },
      ],
    },
  ],
};

export default defineConfig(
  { ignores: ['dist/', 'coverage/'] },
  js.configs.recommended,
  tseslint.configs.strictTypeChecked,
  {
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
  },
  { files: ['src/**/domain/**/*.ts', 'src/**/api/**/*.ts'], rules: noUiImports },
  { files: ['**/*.js'], extends: [tseslint.configs.disableTypeChecked] },
  prettier,
);

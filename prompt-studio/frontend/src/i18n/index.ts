import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

import en from './locales/en.json';
import es from './locales/es.json';
import fr from './locales/fr.json';
import de from './locales/de.json';
import ptBR from './locales/pt-BR.json';
import ar from './locales/ar.json';
import hi from './locales/hi.json';
import ja from './locales/ja.json';

export const SUPPORTED_LANGUAGES = [
  { code: 'en', label: 'English', nativeLabel: 'English' },
  { code: 'es', label: 'Spanish', nativeLabel: 'Español' },
  { code: 'fr', label: 'French', nativeLabel: 'Français' },
  { code: 'de', label: 'German', nativeLabel: 'Deutsch' },
  { code: 'pt-BR', label: 'Portuguese (Brazil)', nativeLabel: 'Português (BR)' },
  { code: 'ar', label: 'Arabic', nativeLabel: 'العربية' },
  { code: 'hi', label: 'Hindi', nativeLabel: 'हिन्दी' },
  { code: 'ja', label: 'Japanese', nativeLabel: '日本語' },
];

export const RTL_LANGUAGES = ['ar'];

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: { translation: en },
      es: { translation: es },
      fr: { translation: fr },
      de: { translation: de },
      'pt-BR': { translation: ptBR },
      ar: { translation: ar },
      hi: { translation: hi },
      ja: { translation: ja },
    },
    fallbackLng: 'en',
    supportedLngs: ['en', 'es', 'fr', 'de', 'pt-BR', 'ar', 'hi', 'ja'],
    interpolation: {
      escapeValue: false,
    },
    detection: {
      order: ['localStorage', 'navigator'],
      lookupLocalStorage: 'genprompt_language',
      caches: ['localStorage'],
    },
  });

export default i18n;

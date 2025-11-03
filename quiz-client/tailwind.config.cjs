/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./index.html",
    "./src/**/*.{js,jsx,ts,tsx}"
  ],
  theme: {
    extend: {
      colors: {
        primary: '#8b5cf6', // ejemplo
        secondary: '#ec4899'
      }
    },
  },
  plugins: [],
};

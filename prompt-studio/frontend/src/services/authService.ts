import api from './api'
import i18n from '../i18n/index'

export interface LoginResponse {
  token: string
  user: {
    id: string
    email: string
    name: string
    profilePicture?: string
    language?: string
  }
}

export interface RegisterResponse {
  token: string
  user: {
    id: string
    email: string
    name: string
    profilePicture?: string
    language?: string
  }
}

class AuthService {
  async login(email: string, password: string): Promise<LoginResponse> {
    const response = await api.post<LoginResponse>('/auth/login', {
      email,
      password
    })
    return response.data
  }

  async register(email: string, password: string, name: string): Promise<RegisterResponse> {
    const response = await api.post<RegisterResponse>('/auth/register', {
      email,
      password,
      name
    })
    return response.data
  }

  async googleLogin(credential: string): Promise<LoginResponse> {
    const response = await api.post<LoginResponse>('/auth/google-login', {
      credential
    })
    return response.data
  }

  async getCurrentUser() {
    const response = await api.get('/auth/me')
    return response.data
  }

  storeAuth(token: string, user: LoginResponse['user']): void {
    localStorage.setItem('authToken', token)
    localStorage.setItem('user', JSON.stringify(user))
    if (user.language) {
      localStorage.setItem('genprompt_language', user.language)
      i18n.changeLanguage(user.language)
    }
  }

  logout(): void {
    localStorage.removeItem('authToken')
    localStorage.removeItem('user')
  }

  isAuthenticated(): boolean {
    return !!localStorage.getItem('authToken')
  }

  getUser() {
    const userStr = localStorage.getItem('user')
    return userStr ? JSON.parse(userStr) : null
  }

  async forgotPassword(email: string): Promise<{ message: string }> {
    const response = await api.post('/auth/forgot-password', { email })
    return response.data
  }

  async resetPassword(token: string, newPassword: string): Promise<{ message: string }> {
    const response = await api.post('/auth/reset-password', {
      token,
      newPassword
    })
    return response.data
  }

  async refreshToken(): Promise<LoginResponse> {
    const response = await api.post<LoginResponse>('/auth/refresh-token')
    return response.data
  }

  async updateLanguage(language: string): Promise<void> {
    await api.put('/auth/language', { language })
  }
}

export default new AuthService()

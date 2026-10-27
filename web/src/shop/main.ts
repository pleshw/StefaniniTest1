import 'bootstrap/dist/css/bootstrap.min.css';
import 'bootstrap-icons/font/bootstrap-icons.min.css';
import { requireElement } from '../shared/ui/dom';
import { mountShop } from './ui/app';

mountShop(requireElement('app'));
